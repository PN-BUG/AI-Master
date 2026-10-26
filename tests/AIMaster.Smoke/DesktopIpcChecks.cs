using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyDesktopIpcChecks() => Task.Run(VerifyDesktopIpcChecksAsync).GetAwaiter().GetResult();

    private static async Task VerifyDesktopIpcChecksAsync()
    {
        var pipeName = "AIMaster-desktop-fixture-" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var events = new ConcurrentQueue<JsonElement>();
        var connections = new ConcurrentQueue<bool>();
        string[] threads = ["fixture-thread"];
        await using var client = new CodexDesktopIpcClient(pipeName);
        client.Start(() => Volatile.Read(ref threads), element => events.Enqueue(element.Clone()), connections.Enqueue);
        await server.WaitForConnectionAsync(deadline.Token);
        var initialize = await ReadDesktopFrameAsync(server, deadline.Token);
        Check(initialize.GetProperty("type").GetString() == "request" &&
              initialize.GetProperty("method").GetString() == "initialize" &&
              initialize.GetProperty("version").GetInt32() == 0,
            "desktop observer initializes the existing named pipe without starting a Codex process");
        await WriteDesktopFrameAsync(server, new
        {
            type = "response", requestId = initialize.GetProperty("requestId").GetString(),
            resultType = "success", method = "initialize", result = new { clientId = "observer" }
        }, deadline.Token, fragmented: true);
        var discovery = await ReadDesktopFrameAsync(server, deadline.Token);
        Check(discovery.GetProperty("method").GetString() == "thread-owner-discovery" &&
              discovery.GetProperty("version").GetInt32() == 1 &&
              discovery.GetProperty("params").GetProperty("hostId").GetString() == "local",
            "observer discovers a thread owner using the read-only coordination request");
        await WriteDesktopFrameAsync(server, new
        {
            type = "response", requestId = discovery.GetProperty("requestId").GetString(),
            resultType = "success", method = "thread-owner-discovery", handledByClientId = "desktop-owner", result = new { }
        }, deadline.Token);
        var following = await ReadDesktopFrameAsync(server, deadline.Token);
        Check(IsFollowing(following, true) && following.GetProperty("targetClientIds")[0].GetString() == "desktop-owner",
            "observer subscribes only as a follower of the discovered desktop owner");

        foreach (var (source, host, version) in new[]
                 { ("wrong-owner", "local", 11), ("desktop-owner", "remote", 11), ("desktop-owner", "local", 10) })
            await WriteDesktopFrameAsync(server, State(source, host, version), deadline.Token);
        await WriteDesktopFrameAsync(server, State("desktop-owner", "local", 11), deadline.Token, fragmented: true);
        await WaitForConnectionFixtureAsync(() => events.Count == 1);
        Check(events.Single().GetProperty("params").GetProperty("change").GetProperty("conversationState")
                .GetProperty("title").GetString() == "审批 🐈",
            "observer decodes fragmented UTF-8 state and rejects foreign owner, host, and protocol versions");

        await WriteDesktopFrameAsync(server, new
        {
            type = "client-discovery-request", requestId = "must-never-own", request = new
            { method = "thread-owner-discovery", version = 1, @params = new { hostId = "local", conversationId = "fixture-thread" } }
        }, deadline.Token);
        var refusal = await ReadDesktopFrameAsync(server, deadline.Token);
        Check(refusal.GetProperty("type").GetString() == "client-discovery-response" &&
              !refusal.GetProperty("response").GetProperty("canHandle").GetBoolean(),
            "observer explicitly refuses to own or handle another client's requests");

        await WriteDesktopFrameAsync(server, new
        {
            type = "request", requestId = "never-answer-approval", method = "thread-follower-command-approval-decision",
            version = 1, @params = new { conversationId = "fixture-thread", requestId = "approval" }
        }, deadline.Token);
        client.RequestSnapshot("fixture-thread");
        Check(IsFollowing(await ReadDesktopFrameAsync(server, deadline.Token), true),
            "observer ignores approval commands and resynchronizes only through its follower subscription");
        Volatile.Write(ref threads, Array.Empty<string>());
        Check(IsFollowing(await ReadDesktopFrameAsync(server, deadline.Token), false),
            "removing a monitored thread releases only the observer's own subscription");
        Check(connections.Contains(true), "desktop connection becomes available after initialization");
        server.Disconnect();
        await WaitForConnectionFixtureAsync(() => connections.Contains(false));
        Check(connections.Last() == false, "desktop pipe EOF immediately invalidates live state");

        await VerifyInvalidDesktopFramesAsync();

        static bool IsFollowing(JsonElement frame, bool value) =>
            frame.GetProperty("type").GetString() == "broadcast" &&
            frame.GetProperty("method").GetString() == "thread-stream-following-changed" &&
            frame.GetProperty("params").GetProperty("following").GetBoolean() == value;

        static object State(string source, string host, int version) => new
        {
            type = "broadcast", method = "thread-stream-state-changed", version,
            sourceClientId = source, targetClientIds = new[] { "observer" },
            @params = new
            {
                hostId = host, conversationId = "fixture-thread",
                change = new { type = "snapshot", revision = 1, conversationState = new { title = "审批 🐈", requests = Array.Empty<object>() } }
            }
        };
    }

    private static async Task VerifyInvalidDesktopFramesAsync()
    {
        foreach (var length in new uint[] { uint.MaxValue, 268435457 })
        {
            var pipeName = "AIMaster-desktop-invalid-" + Guid.NewGuid().ToString("N");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var states = 0;
            await using var client = new CodexDesktopIpcClient(pipeName);
            client.Start(() => Array.Empty<string>(), _ => Interlocked.Increment(ref states), _ => { });
            await server.WaitForConnectionAsync(deadline.Token);
            await ReadDesktopFrameAsync(server, deadline.Token);
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, length);
            await server.WriteAsync(prefix, deadline.Token);
            var buffer = new byte[1];
            Check(await server.ReadAsync(buffer, deadline.Token) == 0 && states == 0,
                "invalid desktop frame lengths close the observer connection without allocating or publishing state");
        }
    }

    private static void VerifyDesktopIpcProbe(string[] args) => VerifyDesktopIpcProbeAsync(args).GetAwaiter().GetResult();

    private static async Task VerifyDesktopIpcProbeAsync(string[] args)
    {
        var suppliedId = args.SkipWhile(arg => arg != "--thread-id").Skip(1).FirstOrDefault();
        if (!Guid.TryParse(suppliedId, out var parsedId))
            throw new ArgumentException("The read-only desktop probe requires --thread-id followed by a thread GUID.");
        var threadId = parsedId.ToString();
        var secondsText = args.SkipWhile(arg => arg != "--seconds").Skip(1).FirstOrDefault();
        var seconds = int.TryParse(secondsText, out var duration) ? Math.Clamp(duration, 1, 15) : 5;
        var state = new CodexDesktopTaskState();
        state.SetTrackedThreads([threadId]);
        var snapshots = 0;
        var patches = 0;
        var connections = 0;
        await using (var client = new CodexDesktopIpcClient())
        {
            state.SnapshotRequired += client.RequestSnapshot;
            client.Start(state.GetTrackedThreadIds, envelope =>
            {
                state.Observe(envelope);
                if (!envelope.TryGetProperty("params", out var parameters) ||
                    !parameters.TryGetProperty("change", out var change)) return;
                var kind = ProbeText(change, "type");
                if (kind == "snapshot")
                {
                    Interlocked.Increment(ref snapshots);
                    var value = change.GetProperty("conversationState");
                    var stateId = ProbeGuid(value, "id");
                    var sessionId = ProbeGuid(value, "sessionId");
                    var requests = value.TryGetProperty("requests", out var list) && list.ValueKind == JsonValueKind.Array
                        ? list : default;
                    var methods = requests.ValueKind == JsonValueKind.Array
                        ? requests.EnumerateArray().Select(request => ProbeText(request, "method"))
                            .Select(method => method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or
                                "item/permissions/requestApproval" or "item/tool/requestUserInput" or "mcpServer/elicitation/request"
                                    ? method : "<other>").Distinct().ToArray()
                        : Array.Empty<string>();
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        probe = "desktop-ipc", kind, version = 11, threadId, stateId, sessionId,
                        stateIdMatchesTrackedThread = stateId == threadId, sessionIdMatchesTrackedThread = sessionId == threadId,
                        revision = ProbeRevision(change), requestCount = requests.ValueKind == JsonValueKind.Array ? requests.GetArrayLength() : -1,
                        requestMethods = methods, status = state.GetStatus(threadId)
                    }));
                }
                else if (kind == "patches")
                {
                    Interlocked.Increment(ref patches);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        probe = "desktop-ipc", kind, version = 11, threadId, revision = ProbeRevision(change),
                        patchCount = change.TryGetProperty("patches", out var items) && items.ValueKind == JsonValueKind.Array
                            ? items.GetArrayLength() : -1,
                        status = state.GetStatus(threadId)
                    }));
                }
            }, connected =>
            {
                if (connected) Interlocked.Increment(ref connections);
                else state.Clear();
                Console.WriteLine(JsonSerializer.Serialize(new { probe = "desktop-ipc", connected }));
            });
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                probe = "desktop-ipc-summary", threadId, seconds, connections = Volatile.Read(ref connections),
                snapshots = Volatile.Read(ref snapshots), patches = Volatile.Read(ref patches),
                connected = client.IsConnected, status = state.GetStatus(threadId)
            }));
            state.SnapshotRequired -= client.RequestSnapshot;
        }

        static string? ProbeText(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

        static string? ProbeGuid(JsonElement value, string key) =>
            Guid.TryParse(ProbeText(value, key), out var guid) ? guid.ToString() : null;

        static long? ProbeRevision(JsonElement value) => value.TryGetProperty("revision", out var revision) &&
            revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var number) ? number : null;
    }

    private static async Task<JsonElement> ReadDesktopFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        Check(length is > 0 and < 1048576, "fixture sees a bounded observer coordination message");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static async Task WriteDesktopFrameAsync(Stream stream, object message, CancellationToken cancellationToken,
        bool fragmented = false)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions
            { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
        json.CopyTo(frame, 4);
        if (fragmented)
        {
            for (var index = 0; index < frame.Length; index += 3)
                await stream.WriteAsync(frame.AsMemory(index, Math.Min(3, frame.Length - index)), cancellationToken);
        }
        else await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
