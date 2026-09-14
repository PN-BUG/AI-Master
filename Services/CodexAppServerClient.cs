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
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private StreamWriter? _input;
    private Task? _readerTask;
    private long _requestId;

    public bool IsConnected => _process is { HasExited: false } && _input != null;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;

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
            if (!string.IsNullOrWhiteSpace(args.Data)) Debug.WriteLine($"[CodexAppServer] {args.Data}");
        };
        _process.BeginErrorReadLine();
        _input = _process.StandardInput;
        _readerTask = ReadLoopAsync(_process.StandardOutput, _lifetime.Token);
        try
        {
            await RequestAsync("initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "software_toolkit_ai_manager",
                    ["title"] = "AIMaster",
                    ["version"] = "1.0.0"
                }
            }, cancellationToken, TimeSpan.FromSeconds(60));
            await NotifyAsync("initialized", new JsonObject(), cancellationToken);
        }
        catch
        {
            try { _input.Close(); } catch { }
            if (_process is { HasExited: false })
                try { _process.Kill(entireProcessTree: true); } catch { }
            _process.Dispose();
            _process = null;
            _input = null;
            throw;
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

    public Task<JsonElement> RequestAsync(string method, JsonNode? parameters = null,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        if (_input == null) throw new InvalidOperationException("Codex App Server 尚未连接。");
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        return SendRequestAsync(id, method, parameters, completion, cancellationToken,
            timeout ?? TimeSpan.FromSeconds(30));
    }

    private async Task<JsonElement> SendRequestAsync(long id, string method, JsonNode? parameters,
        TaskCompletionSource<JsonElement> completion, CancellationToken cancellationToken, TimeSpan timeout)
    {
        var message = new JsonObject { ["method"] = method, ["id"] = id };
        if (parameters != null) message["params"] = parameters;
        try
        {
            await WriteAsync(message, cancellationToken);
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

    private Task NotifyAsync(string method, JsonNode parameters, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject { ["method"] = method, ["params"] = parameters }, cancellationToken);

    private async Task WriteAsync(JsonNode message, CancellationToken cancellationToken)
    {
        if (_input == null) throw new InvalidOperationException("Codex App Server 输入流不可用。");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _input.WriteLineAsync(message.ToJsonString()).WaitAsync(cancellationToken);
            await _input.FlushAsync().WaitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken cancellationToken)
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
                    Debug.WriteLine($"[CodexAppServer] Ignored non-JSON stdout: {line[..Math.Min(line.Length, 160)]}");
                    continue;
                }
                foreach (var json in messages)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(json);
                        HandleMessage(document.RootElement);
                    }
                    catch (JsonException ex)
                    {
                        // App-server occasionally writes an adjacent diagnostic on stdout.
                        // A malformed fragment must not tear down the whole JSONL connection.
                        Debug.WriteLine($"[CodexAppServer] Ignored malformed stdout fragment: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            foreach (var item in _pending.Values) item.TrySetException(ex);
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
                foreach (var item in _pending.Values)
                    item.TrySetException(new IOException("Codex App Server 连接已关闭。"));
        }
    }

    private void HandleMessage(JsonElement root)
    {
        if (!TryReadResponseId(root, out var id)) return;
        if (!_pending.TryGetValue(id, out var completion)) return;
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : error.GetRawText();
            completion.TrySetException(new InvalidOperationException(message ?? "Codex App Server 请求失败。"));
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
        _lifetime.Cancel();
        try { _input?.Close(); } catch { }
        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        if (_readerTask != null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        _process?.Dispose();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }
}
