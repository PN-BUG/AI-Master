using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIMaster.Services;

/// <summary>
/// Minimal JSONL client for the stable Codex app-server protocol.
/// It reuses the user's existing Codex authentication and never reads token files directly.
/// </summary>
internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private sealed record PendingRequest(long ConnectionId, TaskCompletionSource<JsonElement> Completion);

    private sealed class AppServerRequestException(string message, long? code) : InvalidOperationException(message)
    {
        public long? Code { get; } = code;
    }

    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private StreamWriter? _input;
    private Task? _readerTask;
    private long _requestId;
    private long _connectionId;
    private long _initializedConnectionId = -1;
    private int _disposed;

    public bool IsConnected => Volatile.Read(ref _initializedConnectionId) == Volatile.Read(ref _connectionId) &&
                               IsTransportAvailable;

    private bool IsTransportAvailable
    {
        get
        {
            try
            {
                return !_lifetime.IsCancellationRequested && _process is { HasExited: false } &&
                       _input?.BaseStream.CanWrite == true && _readerTask is { IsCompleted: false };
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return false; }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
        await GetConnectedTransportAsync(cancellationToken);

    private async Task<(long ConnectionId, StreamWriter Input)> GetConnectedTransportAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _connectLock.WaitAsync(linked.Token);
        try
        {
            if (IsConnected) return (Volatile.Read(ref _connectionId), _input!);
            CloseTransport();
            linked.Token.ThrowIfCancellationRequested();
            var executable = ResolveCodexExecutable();
            if (executable == null)
                throw new FileNotFoundException(
                    "未找到 Codex CLI。已检查 CODEX_EXECUTABLE、OpenAI Codex 桌面安装目录、PATH、npm 和常用用户安装目录。");
            var isCommandShim = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
            var info = new ProcessStartInfo
            {
                FileName = isCommandShim ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : executable,
                Arguments = isCommandShim
                    ? $"/d /s /c \"\"{executable}\" app-server --listen stdio://\""
                    : "app-server --listen stdio://",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            _process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 codex app-server。");
            _process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data)) Debug.WriteLine("[CodexAppServer] Server diagnostic received.");
            };
            _process.BeginErrorReadLine();
            _input = _process.StandardInput;
            var connectionId = Volatile.Read(ref _connectionId);
            _readerTask = ReadLoopAsync(_process.StandardOutput, connectionId, _lifetime.Token);
            try
            {
                await SendRequestAsync("initialize", new JsonObject
                {
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "software_toolkit_ai_manager",
                        ["title"] = "AIMaster",
                        ["version"] = "1.0.0"
                    }
                }, connectionId, _input, linked.Token, TimeSpan.FromSeconds(60), requireInitialized: false);
                await WriteAsync(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() },
                    connectionId, _input, linked.Token, requireInitialized: false);
                if (!IsTransportAvailable) throw new IOException("Codex App Server 连接已关闭。");
                Volatile.Write(ref _initializedConnectionId, connectionId);
                return (connectionId, _input);
            }
            catch
            {
                CloseTransport();
                throw;
            }
        }
        finally { _connectLock.Release(); }
    }

    private void CloseTransport()
    {
        var connectionId = Interlocked.Increment(ref _connectionId) - 1;
        Interlocked.Exchange(ref _initializedConnectionId, -1);
        var input = _input;
        var process = _process;
        _input = null;
        _process = null;
        _readerTask = null;
        FailPendingRequests(connectionId, new IOException("Codex App Server 连接已关闭。"));
        try { input?.Close(); } catch { }
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
        }
    }

    internal static string? ResolveCodexExecutable() => ResolveCodexExecutableFrom(
        Environment.GetEnvironmentVariable("CODEX_EXECUTABLE"),
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string? ResolveCodexExecutableFrom(string? configuredPath, string? pathValue,
        string localAppData, string roamingAppData, string userProfile)
    {
        if (IsRunnableFile(configuredPath))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath!));

        var adjacent = Path.Combine(AppContext.BaseDirectory, "codex.exe");
        if (File.Exists(adjacent)) return adjacent;

        // Codex desktop keeps the CLI in a versioned bin/<hash>/ directory. GUI apps often
        // inherit a PATH that does not contain this dynamically-created directory.
        var desktopBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (Directory.Exists(desktopBin))
        {
            try
            {
                var desktopCli = Directory.EnumerateFiles(desktopBin, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                    .FirstOrDefault();
                if (desktopCli != null) return desktopCli;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        foreach (var directory in (pathValue ?? string.Empty).Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleanDirectory = directory.Trim('"');
            foreach (var fileName in new[] { "codex.exe", "codex.cmd" })
            {
                try
                {
                    var candidate = Path.Combine(cleanDirectory, fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(roamingAppData, "npm", "codex.cmd"),
                     Path.Combine(userProfile, ".local", "bin", "codex.exe"),
                     Path.Combine(userProfile, ".cargo", "bin", "codex.exe")
                 })
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static bool IsRunnableFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Environment.ExpandEnvironmentVariables(path));

    public async Task<JsonElement> RequestAsync(string method, JsonNode? parameters = null,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var requestTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        deadline.CancelAfter(requestTimeout);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var (connectionId, input) = await GetConnectedTransportAsync(deadline.Token);
                    return await SendRequestAsync(method, parameters?.DeepClone(), connectionId, input,
                        deadline.Token, requestTimeout);
                }
                catch (Exception ex) when (attempt == 0 && method == "account/rateLimits/read" &&
                                           !deadline.IsCancellationRequested && IsTransientReadFailure(ex))
                {
                    // Only this read is safe to repeat. Keep the same total deadline and
                    // leave authentication, throttling, and all write operations to callers.
                    await Task.Delay(TimeSpan.FromMilliseconds(400), deadline.Token);
                }
            }
        }
        catch (OperationCanceledException ex) when (!linked.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Codex App Server 请求 {method} 超时（{requestTimeout.TotalSeconds:0} 秒）。", ex);
        }
    }

    private static bool IsTransientReadFailure(Exception exception)
    {
        if (exception is IOException or ObjectDisposedException) return true;
        if (exception is not AppServerRequestException request) return false;
        if (request.Code is 401 or 403 or 429 or -32600 or -32601 or -32602) return false;
        var message = request.Message;
        if (new[] { "401", "403", "429", "unauthorized", "forbidden", "authentication", "unauthenticated",
                    "not logged in", "login required", "too many requests", "rate limit exceeded", "rate limited",
                    "rate_limit" }.Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase)))
            return false;
        return new[] { "error sending request for url", "connection reset", "connection closed", "connection refused",
                       "connection aborted", "temporarily unavailable", "timed out", "http 502", "http 503", "http 504" }
            .Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private Task<JsonElement> SendRequestAsync(string method, JsonNode? parameters, long connectionId,
        StreamWriter input, CancellationToken cancellationToken, TimeSpan timeout, bool requireInitialized = true)
    {
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingRequest(connectionId, completion);
        return SendRequestAsync(id, method, parameters, connectionId, input, completion, cancellationToken, timeout,
            requireInitialized);
    }

    private async Task<JsonElement> SendRequestAsync(long id, string method, JsonNode? parameters,
        long connectionId, StreamWriter input, TaskCompletionSource<JsonElement> completion,
        CancellationToken cancellationToken, TimeSpan timeout, bool requireInitialized)
    {
        var message = new JsonObject { ["method"] = method, ["id"] = id };
        if (parameters != null) message["params"] = parameters;
        try
        {
            await WriteAsync(message, connectionId, input, cancellationToken, requireInitialized);
            try
            {
                return await completion.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"Codex App Server 请求 {method} 超时（{timeout.TotalSeconds:0} 秒）。", ex);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteAsync(JsonNode message, long connectionId, StreamWriter input,
        CancellationToken cancellationToken, bool requireInitialized)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (connectionId != Volatile.Read(ref _connectionId) || !input.BaseStream.CanWrite ||
                (requireInitialized && connectionId != Volatile.Read(ref _initializedConnectionId)))
                throw new IOException("Codex App Server 输入流不可用。");
            await input.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken);
            await input.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            InvalidateConnection(connectionId, new IOException("Codex App Server 输入流已关闭。", ex));
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, long connectionId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                var messages = ExtractJsonMessages(line);
                if (messages.Count == 0)
                {
                    Debug.WriteLine("[CodexAppServer] Ignored non-JSON stdout.");
                    continue;
                }
                foreach (var json in messages)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(json);
                        HandleMessage(document.RootElement, connectionId);
                    }
                    catch (JsonException)
                    {
                        // App-server occasionally writes an adjacent diagnostic on stdout.
                        // A malformed fragment must not tear down the whole JSONL connection.
                        Debug.WriteLine("[CodexAppServer] Ignored malformed stdout fragment.");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            InvalidateConnection(connectionId, new IOException("Codex App Server 连接读取失败。", ex));
        }
        finally
        {
            InvalidateConnection(connectionId, new IOException("Codex App Server 连接已关闭。"));
        }
    }

    private void InvalidateConnection(long connectionId, Exception exception)
    {
        Interlocked.CompareExchange(ref _initializedConnectionId, -1, connectionId);
        FailPendingRequests(connectionId, exception);
    }

    private void FailPendingRequests(long connectionId, Exception exception)
    {
        foreach (var item in _pending.Values)
            if (item.ConnectionId == connectionId) item.Completion.TrySetException(exception);
    }

    private void HandleMessage(JsonElement root, long connectionId)
    {
        if (!TryReadResponseId(root, out var id)) return;
        if (!_pending.TryGetValue(id, out var pending) || pending.ConnectionId != connectionId) return;
        var completion = pending.Completion;
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : error.GetRawText();
            var code = error.TryGetProperty("code", out var codeValue) && JsonProtocolValue.TryGetInt64(codeValue, out var number)
                ? number : (long?)null;
            completion.TrySetException(new AppServerRequestException(message ?? "Codex App Server 请求失败。", code));
        }
        else if (root.TryGetProperty("result", out var result))
        {
            completion.TrySetResult(result.Clone());
        }
    }

    internal static bool TryReadResponseId(JsonElement root, out long id)
    {
        id = 0;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("id", out var idElement) &&
               JsonProtocolValue.TryGetInt64(idElement, out id);
    }

    internal static IReadOnlyList<string> ExtractJsonMessages(string line)
    {
        var messages = new List<string>();
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];
            if (start < 0)
            {
                if (current == '{')
                {
                    start = index;
                    depth = 1;
                    inString = false;
                    escaped = false;
                }
                continue;
            }

            if (inString)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') inString = false;
                continue;
            }

            if (current == '"') inString = true;
            else if (current == '{') depth++;
            else if (current == '}' && --depth == 0)
            {
                messages.Add(line.Substring(start, index - start + 1));
                start = -1;
            }
        }
        return messages;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _connectLock.WaitAsync();
        try
        {
            var readerTask = _readerTask;
            CloseTransport();
            if (readerTask != null)
            {
                try { await readerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
        }
        finally { _connectLock.Release(); }
        // In-flight callers may still be releasing either gate after lifetime cancellation.
        // SemaphoreSlim has no unmanaged resources unless its wait handle was requested.
        _lifetime.Dispose();
    }
}
