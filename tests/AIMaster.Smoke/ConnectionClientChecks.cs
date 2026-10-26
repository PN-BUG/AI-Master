using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIMaster.Services;

internal static partial class Program
{
    private const string ConnectionFixtureEnvironment = "AIMASTER_CONNECTION_TEST_FIXTURE";

    private static void VerifyConnectionClientChecks() => VerifyConnectionClientChecksAsync().GetAwaiter().GetResult();

    private static async Task VerifyConnectionClientChecksAsync()
    {
        // Every fixture runs our own apphost as a local JSONL server. No Codex executable,
        // authentication, proxy, or network endpoint participates in these checks.
        await WithConnectionFixtureAsync("slow-initialize", async (client, directory) =>
        {
            var first = client.ConnectAsync();
            await WaitForConnectionFixtureAsync(() => Directory.GetFiles(directory, "*.started").Length > 0);
            Check(!client.IsConnected, "Codex connection stays unavailable until initialization completes");
            await Task.WhenAll(first, client.ConnectAsync(), client.ConnectAsync());
            var result = await client.RequestAsync("thread/list");
            Check(result.GetProperty("initialized").GetBoolean() &&
                  Directory.GetFiles(directory, "*.started").Length == 1,
                "concurrent connection attempts share one fully initialized app-server process");
        });

        await WithConnectionFixtureAsync("slow-initialize", async (client, directory) =>
        {
            var first = client.ConnectAsync();
            await WaitForConnectionFixtureAsync(() => Directory.GetFiles(directory, "*.started").Length > 0);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await client.ConnectAsync(cancellation.Token);
                throw new InvalidOperationException("Canceled connection waiter unexpectedly succeeded.");
            }
            catch (OperationCanceledException) { }
            await first;
            var result = await client.RequestAsync("thread/list");
            Check(result.GetProperty("initialized").GetBoolean() &&
                  Directory.GetFiles(directory, "*.started").Length == 1,
                "canceling a queued connection waiter preserves another caller's initialization");
        });

        await WithConnectionFixtureAsync("close-stdout-once", async (client, directory) =>
        {
            await client.ConnectAsync();
            await client.RequestAsync("thread/list");
            await WaitForConnectionFixtureAsync(() => !client.IsConnected);
            var result = await client.RequestAsync("thread/list");
            Check(result.GetProperty("initialized").GetBoolean() &&
                  Directory.GetFiles(directory, "*.started").Length == 2,
                "stdout EOF invalidates a live server and the next request reconnects");
        });

        await WithConnectionFixtureAsync("normal", async (client, directory) =>
        {
            await client.ConnectAsync();
            // A broken redirected stdin is an independent failure from process exit/EOF.
            ((StreamWriter)typeof(CodexAppServerClient).GetField("_input", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client)!).Close();
            var result = await client.RequestAsync("thread/list");
            Check(result.GetProperty("initialized").GetBoolean() &&
                  Directory.GetFiles(directory, "*.started").Length == 2,
                "a closed server input reconnects before the next request is sent");
        });

        await WithConnectionFixtureAsync("network-once", async (client, directory) =>
        {
            var result = await client.RequestAsync("account/rateLimits/read");
            Check(result.GetProperty("initialized").GetBoolean() && CountConnectionRequests(directory) == 2,
                "a transient quota network failure retries once and recovers");
        });

        await WithConnectionFixtureAsync("exit-once", async (client, directory) =>
        {
            var result = await client.RequestAsync("account/rateLimits/read");
            Check(result.GetProperty("initialized").GetBoolean() && CountConnectionRequests(directory) == 2 &&
                  Directory.GetFiles(directory, "*.started").Length == 2,
                "a quota read reconnects once when its server exits during the request");
        });

        foreach (var mode in new[] { "unauthorized", "forbidden", "rate-limit", "invalid-method", "network-always" })
        {
            await WithConnectionFixtureAsync(mode, async (client, directory) =>
            {
                await client.ConnectAsync();
                await ExpectConnectionFailureAsync(() => client.RequestAsync("account/rateLimits/read"));
                Check(CountConnectionRequests(directory) == (mode == "network-always" ? 2 : 1),
                    $"quota retry is bounded and does not retry {mode}");
            });
        }

        await WithConnectionFixtureAsync("network-always", async (client, directory) =>
        {
            await client.ConnectAsync();
            await ExpectConnectionFailureAsync(() => client.RequestAsync("turn/interrupt", new JsonObject
            {
                ["threadId"] = "local-fixture", ["turnId"] = "local-fixture"
            }));
            Check(CountConnectionRequests(directory) == 1,
                "a write operation is never repeated after a network-shaped server error");
        });

        await WithConnectionFixtureAsync("network-always", async (client, directory) =>
        {
            await client.ConnectAsync();
            using var cancellation = new CancellationTokenSource();
            var pending = client.RequestAsync("account/rateLimits/read", cancellationToken: cancellation.Token);
            await WaitForConnectionFixtureAsync(() => CountConnectionRequests(directory) > 0);
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();
            try
            {
                await pending;
                throw new InvalidOperationException("Canceled quota request unexpectedly succeeded.");
            }
            catch (OperationCanceledException) { }
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(1) && CountConnectionRequests(directory) == 1,
                "cancellation interrupts quota retry delay without another request");
        });

        await WithConnectionFixtureAsync("network-always", async (client, directory) =>
        {
            await client.ConnectAsync();
            var elapsed = Stopwatch.StartNew();
            try
            {
                await client.RequestAsync("account/rateLimits/read", timeout: TimeSpan.FromMilliseconds(200));
                throw new InvalidOperationException("Timed out quota request unexpectedly succeeded.");
            }
            catch (TimeoutException) { }
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(1) && CountConnectionRequests(directory) == 1,
                "quota retry shares the caller's total timeout budget");
        });
    }

    private static async Task WithConnectionFixtureAsync(string mode,
        Func<CodexAppServerClient, string, Task> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster-connection-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "mode"), mode);
        var previousExecutable = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
        var previousFixture = Environment.GetEnvironmentVariable(ConnectionFixtureEnvironment);
        try
        {
            Environment.SetEnvironmentVariable("CODEX_EXECUTABLE", Environment.ProcessPath);
            Environment.SetEnvironmentVariable(ConnectionFixtureEnvironment, directory);
            await using var client = new CodexAppServerClient();
            await check(client, directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_EXECUTABLE", previousExecutable);
            Environment.SetEnvironmentVariable(ConnectionFixtureEnvironment, previousFixture);
            foreach (var started in Directory.GetFiles(directory, "*.started"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(started), out var processId)) continue;
                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int CountConnectionRequests(string directory) => Directory.GetFiles(directory, "*.requests")
        .Sum(path => File.ReadAllLines(path).Length);

    private static async Task WaitForConnectionFixtureAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Controlled connection fixture did not reach its expected state.");
            await Task.Delay(20);
        }
    }

    private static async Task ExpectConnectionFailureAsync(Func<Task<JsonElement>> operation)
    {
        try { await operation(); }
        catch (InvalidOperationException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException("Controlled server error unexpectedly succeeded.");
    }

    private static bool TryRunConnectionClientServer(string[] args)
    {
        var directory = Environment.GetEnvironmentVariable(ConnectionFixtureEnvironment);
        if (args.Length == 0 || args[0] != "app-server" || string.IsNullOrWhiteSpace(directory)) return false;
        var mode = File.ReadAllText(Path.Combine(directory, "mode"));
        var processId = Environment.ProcessId;
        File.WriteAllText(Path.Combine(directory, processId + ".started"), "started");
        var launch = Directory.GetFiles(directory, "*.started").Length;
        var initialized = false;
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "initialized") { initialized = true; continue; }
            var id = root.GetProperty("id").GetInt64();
            JsonObject response;
            if (method == "initialize")
            {
                if (mode == "slow-initialize") Thread.Sleep(500);
                response = new JsonObject { ["id"] = id, ["result"] = new JsonObject() };
            }
            else
            {
                File.AppendAllText(Path.Combine(directory, processId + ".requests"), method + Environment.NewLine);
                var attempt = CountConnectionRequests(directory);
                if (mode == "exit-once" && attempt == 1) return true;
                var message = !initialized ? "request before initialized" : mode switch
                {
                    "network-once" when attempt == 1 => "failed to fetch codex rate limits: error sending request for url (https://chatgpt.com/backend-api/wham/usage)",
                    "network-always" => "failed to fetch codex rate limits: error sending request for url (https://chatgpt.com/backend-api/wham/usage)",
                    "unauthorized" => "HTTP 401 Unauthorized: failed to fetch codex rate limits: error sending request for url",
                    "forbidden" => "HTTP 403 Forbidden: failed to fetch codex rate limits: error sending request for url",
                    "rate-limit" => "HTTP 429 Too Many Requests: failed to fetch codex rate limits: error sending request for url",
                    "invalid-method" => "Method not found",
                    _ => null
                };
                response = message == null
                    ? new JsonObject { ["id"] = id, ["result"] = new JsonObject { ["initialized"] = initialized } }
                    : new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message } };
                if (mode == "metadata-only" && method == "thread/read")
                {
                    File.WriteAllText(Path.Combine(directory, "thread-read.json"), root.GetProperty("params").GetRawText());
                    response = new JsonObject { ["id"] = id, ["result"] = new JsonObject
                    {
                        ["thread"] = new JsonObject
                        {
                            ["name"] = "Metadata title", ["model"] = "metadata-model",
                            ["path"] = Path.Combine(directory, "metadata.jsonl"),
                            ["status"] = new JsonObject { ["type"] = "idle" }
                        }
                    }};
                }
            }
            Console.WriteLine(response.ToJsonString());
            Console.Out.Flush();
            if (mode == "close-stdout-once" && launch == 1 && method != "initialize")
            {
                CloseConnectionFixtureHandle(GetConnectionFixtureStdHandle(-11));
                Thread.Sleep(30000);
                return true;
            }
        }
        return true;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetStdHandle")]
    private static extern IntPtr GetConnectionFixtureStdHandle(int standardHandle);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseConnectionFixtureHandle(IntPtr handle);
}
