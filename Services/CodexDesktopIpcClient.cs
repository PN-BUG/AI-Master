using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace AIMaster.Services;

/// <summary>
/// Observes the running desktop's versioned, local thread follower protocol.
/// This client never claims thread ownership, executes tools, or answers approvals.
/// The protocol is internal to Codex desktop; incompatible messages are ignored.
/// </summary>
internal sealed class CodexDesktopIpcClient(string pipeName = "codex-ipc") : IAsyncDisposable
{
    private const int MaxFrameBytes = 268435456;
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private sealed record PendingRequest(string Method, string? ThreadId, DateTimeOffset CreatedAt);

    private sealed class Session(NamedPipeClientStream pipe)
    {
        public NamedPipeClientStream Pipe { get; } = pipe;
        public string ClientId { get; set; } = "initializing-client";
        public bool Initialized { get; set; }
        public Dictionary<string, PendingRequest> Pending { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Owners { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> LastDiscovery { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Desired { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<string, byte> _snapshotRequests = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private NamedPipeClientStream? _activePipe;
    private Task? _run;
    private int _connected;
    private int _disposed;

    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    // onStateChanged must consume the JsonElement synchronously. Its document is
    // disposed immediately afterward so full conversation transcripts aren't retained.
    public void Start(Func<IReadOnlyList<string>> threadIds, Action<JsonElement> onStateChanged,
        Action<bool> onConnectionChanged)
    {
        ArgumentNullException.ThrowIfNull(threadIds);
        ArgumentNullException.ThrowIfNull(onStateChanged);
        ArgumentNullException.ThrowIfNull(onConnectionChanged);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_run != null) return;
            _run = Task.Run(() => RunAsync(threadIds, onStateChanged, onConnectionChanged));
        }
    }

    public void RequestSnapshot(string threadId)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(threadId)) return;
        _snapshotRequests[threadId] = 0;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(Func<IReadOnlyList<string>> threadIds, Action<JsonElement> onStateChanged,
        Action<bool> onConnectionChanged)
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try { await RunSessionAsync(threadIds, onStateChanged, onConnectionChanged, _lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Deliberately exclude message bodies, command arguments and credentials.
                Debug.WriteLine($"[CodexDesktopIpc] Connection unavailable ({exception.GetType().Name}).");
            }
            finally
            {
                if (Interlocked.Exchange(ref _connected, 0) != 0) NotifyConnection(false, onConnectionChanged);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunSessionAsync(Func<IReadOnlyList<string>> threadIds, Action<JsonElement> onStateChanged,
        Action<bool> onConnectionChanged, CancellationToken cancellationToken)
    {
        using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = sessionLifetime.Token;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        lock (_gate) _activePipe = pipe;
        Task<JsonDocument>? read = null;
        try
        {
            await pipe.ConnectAsync(1500, token);
            var session = new Session(pipe);
            var startedAt = DateTimeOffset.UtcNow;
            await SendRequestAsync(session, "initialize", null, token);
            read = ReadFrameAsync(pipe, token);
            var tick = Task.Delay(ReconcileInterval, token);
            var wake = _wake.WaitAsync(token);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var completed = await Task.WhenAny(read, tick, wake);
                if (completed == read)
                {
                    using var document = await read;
                    await HandleMessageAsync(session, document.RootElement, onStateChanged, onConnectionChanged, token);
                    read = ReadFrameAsync(pipe, token);
                }
                else if (completed == tick)
                {
                    await tick;
                    if (!session.Initialized && DateTimeOffset.UtcNow - startedAt > RequestTimeout)
                        throw new IOException("Desktop IPC initialization timed out.");
                    if (session.Initialized) await ReconcileAsync(session, threadIds, onStateChanged, token);
                    tick = Task.Delay(ReconcileInterval, token);
                }
                else
                {
                    await wake;
                    if (session.Initialized) await ReconcileAsync(session, threadIds, onStateChanged, token);
                    wake = _wake.WaitAsync(token);
                }
                if (session.Initialized && session.Desired.Count == 0)
                    await ReconcileAsync(session, threadIds, onStateChanged, token);
            }
        }
        finally
        {
            sessionLifetime.Cancel();
            pipe.Dispose();
            if (read != null)
            {
                try { (await read).Dispose(); }
                catch { }
            }
            lock (_gate) { if (ReferenceEquals(_activePipe, pipe)) _activePipe = null; }
        }
    }

    private async Task ReconcileAsync(Session session, Func<IReadOnlyList<string>> threadIds,
        Action<JsonElement> onStateChanged, CancellationToken cancellationToken)
    {
        var desired = threadIds().Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var removed in session.Desired.Except(desired).ToArray())
        {
            if (session.Owners.Remove(removed, out var owner))
                await SendFollowingAsync(session, removed, owner, false, cancellationToken);
            session.LastDiscovery.Remove(removed);
        }
        session.Desired = desired;
        var now = DateTimeOffset.UtcNow;
        foreach (var (requestId, pending) in session.Pending.ToArray())
        {
            if (now - pending.CreatedAt <= RequestTimeout) continue;
            session.Pending.Remove(requestId);
            if (pending.ThreadId != null) InvalidateOwner(session, pending.ThreadId, onStateChanged);
        }
        foreach (var threadId in _snapshotRequests.Keys)
        {
            if (!_snapshotRequests.TryRemove(threadId, out _)) continue;
            if (desired.Contains(threadId) && session.Owners.TryGetValue(threadId, out var owner))
                await SendFollowingAsync(session, threadId, owner, true, cancellationToken);
        }
        foreach (var threadId in desired)
        {
            if (session.Pending.Values.Any(pending => pending.ThreadId == threadId) ||
                session.LastDiscovery.TryGetValue(threadId, out var last) && now - last < ReconcileInterval) continue;
            session.LastDiscovery[threadId] = now;
            await SendRequestAsync(session, "thread-owner-discovery", threadId, cancellationToken);
        }
    }

    private async Task HandleMessageAsync(Session session, JsonElement message, Action<JsonElement> onStateChanged,
        Action<bool> onConnectionChanged, CancellationToken cancellationToken)
    {
        if (message.ValueKind != JsonValueKind.Object) return;
        switch (StringProperty(message, "type"))
        {
            case "client-discovery-request":
                if (StringProperty(message, "requestId") is { } discoveryId)
                    await WriteFrameAsync(session.Pipe, new
                    {
                        type = "client-discovery-response", requestId = discoveryId, response = new { canHandle = false }
                    }, cancellationToken);
                return;
            case "response":
                if (StringProperty(message, "requestId") is not { } requestId ||
                    !session.Pending.Remove(requestId, out var pending)) return;
                if (pending.Method == "initialize")
                {
                    if (StringProperty(message, "resultType") != "success" ||
                        StringProperty(message, "method") != "initialize" ||
                        !message.TryGetProperty("result", out var result) ||
                        StringProperty(result, "clientId") is not { Length: > 0 } clientId)
                        throw new IOException("Desktop IPC initialization was rejected.");
                    session.ClientId = clientId;
                    session.Initialized = true;
                    Interlocked.Exchange(ref _connected, 1);
                    NotifyConnection(true, onConnectionChanged);
                    return;
                }
                var threadId = pending.ThreadId!;
                if (!session.Desired.Contains(threadId)) return;
                if (StringProperty(message, "resultType") != "success" ||
                    StringProperty(message, "method") != "thread-owner-discovery" ||
                    StringProperty(message, "handledByClientId") is not { Length: > 0 } ownerId || ownerId == session.ClientId)
                {
                    InvalidateOwner(session, threadId, onStateChanged);
                    return;
                }
                if (session.Owners.TryGetValue(threadId, out var previousOwner))
                {
                    if (previousOwner == ownerId) return;
                    InvalidateOwner(session, threadId, onStateChanged);
                    await SendFollowingAsync(session, threadId, previousOwner, false, cancellationToken);
                }
                session.Owners[threadId] = ownerId;
                await SendFollowingAsync(session, threadId, ownerId, true, cancellationToken);
                return;
            case "broadcast":
                if (!session.Initialized || !IsTargetedTo(message, session.ClientId) ||
                    StringProperty(message, "sourceClientId") is not { Length: > 0 } source ||
                    !message.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                    return;
                var method = StringProperty(message, "method");
                if (method == "client-status-changed" && IntegerProperty(message, "version") == 0 &&
                    StringProperty(parameters, "clientId") == source && StringProperty(parameters, "status") == "disconnected")
                {
                    var affected = session.Owners.Where(pair => pair.Value == source).Select(pair => pair.Key).ToArray();
                    if (affected.Length == 0) return;
                    foreach (var id in affected) { session.Owners.Remove(id); session.LastDiscovery.Remove(id); }
                    NotifyState(message, onStateChanged);
                    return;
                }
                if (StringProperty(parameters, "hostId") != "local" ||
                    StringProperty(parameters, "conversationId") is not { } conversationId ||
                    !session.Desired.Contains(conversationId) ||
                    !session.Owners.TryGetValue(conversationId, out var expectedOwner) || expectedOwner != source) return;
                if (method == "thread-stream-following-status-requested" && IntegerProperty(message, "version") == 1)
                {
                    await SendFollowingAsync(session, conversationId, source, true, cancellationToken);
                    return;
                }
                if (method == "thread-stream-state-changed" && IntegerProperty(message, "version") == 11 &&
                    parameters.TryGetProperty("change", out var change) && change.ValueKind == JsonValueKind.Object &&
                    StringProperty(change, "type") is "snapshot" or "patches") NotifyState(message, onStateChanged);
                return;
        }
    }

    private static void InvalidateOwner(Session session, string threadId, Action<JsonElement> onStateChanged)
    {
        if (!session.Owners.Remove(threadId, out var owner)) return;
        // Internal invalidation only: never sent to Codex or treated as a real approval event.
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "broadcast", method = "thread-stream-owner-unavailable", sourceClientId = owner,
            @params = new { hostId = "local", conversationId = threadId }
        }));
        NotifyState(document.RootElement, onStateChanged);
    }

    private static async Task SendRequestAsync(Session session, string method, string? threadId,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        session.Pending[requestId] = new PendingRequest(method, threadId, DateTimeOffset.UtcNow);
        if (method == "initialize")
            await WriteFrameAsync(session.Pipe, new
            {
                type = "request", requestId, sourceClientId = session.ClientId, version = 0, method,
                @params = new { clientType = "ai-master-observer" }
            }, cancellationToken);
        else if (method == "thread-owner-discovery")
            await WriteFrameAsync(session.Pipe, new
            {
                type = "request", requestId, sourceClientId = session.ClientId, version = 1, method,
                @params = new { hostId = "local", conversationId = threadId }
            }, cancellationToken);
        else throw new InvalidOperationException("Observer requests are restricted to initialization and owner discovery.");
    }

    private static Task SendFollowingAsync(Session session, string threadId, string owner, bool following,
        CancellationToken cancellationToken) => WriteFrameAsync(session.Pipe, new
    {
        type = "broadcast", method = "thread-stream-following-changed", sourceClientId = session.ClientId,
        targetClientIds = new[] { owner }, version = 1,
        @params = new { hostId = "local", conversationId = threadId, following }
    }, cancellationToken);

    private static async Task WriteFrameAsync(Stream stream, object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)bytes.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length == 0 || length > MaxFrameBytes) throw new IOException("Invalid desktop IPC frame length.");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
    }

    private static bool IsTargetedTo(JsonElement message, string clientId) =>
        !message.TryGetProperty("targetClientIds", out var targets) || targets.ValueKind == JsonValueKind.Null ||
        targets.ValueKind == JsonValueKind.Array && targets.EnumerateArray().Any(target =>
            target.ValueKind == JsonValueKind.String && target.GetString() == clientId);

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntegerProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) ? number : null;

    private static void NotifyState(JsonElement element, Action<JsonElement> callback)
    {
        try { callback(element); }
        catch (Exception exception) { Debug.WriteLine($"[CodexDesktopIpc] State callback failed ({exception.GetType().Name})."); }
    }

    private static void NotifyConnection(bool connected, Action<bool> callback)
    {
        try { callback(connected); }
        catch (Exception exception) { Debug.WriteLine($"[CodexDesktopIpc] Connection callback failed ({exception.GetType().Name})."); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        Task? run;
        lock (_gate) { _activePipe?.Dispose(); run = _run; }
        if (run != null) await run;
        _snapshotRequests.Clear();
        _wake.Dispose();
        _lifetime.Dispose();
    }
}
