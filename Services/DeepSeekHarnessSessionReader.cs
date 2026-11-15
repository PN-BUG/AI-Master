using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

/// <summary>
/// Where the DeepSeek Harness keeps its durable state: the desktop profile under <c>~/.dsh</c>.
/// The root is overridable with <c>DSH_HOME</c> so tests and portable copies never read real history.
/// </summary>
internal static class DeepSeekHarnessPaths
{
    public static string GetHome()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_HOME");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh")
            : configured;
    }

    /// <summary>
    /// The harness checkpoints one projection record per session here. The session log stays
    /// authoritative, but the listing values (title, model, open turn, pending questions) are what
    /// the harness itself reads for its session list, so they are the accurate local task state.
    /// </summary>
    public static string GetSessionCacheDirectory(string? home = null) =>
        Path.Combine(string.IsNullOrWhiteSpace(home) ? GetHome() : home,
            "storages", "session_projcache", "sessions");
}

/// <summary>
/// Reads the DeepSeek Harness session-projection cache. The desktop app stores conversation text in
/// the cloud, so its LocalStorage only has drafts and question state; the harness itself checkpoints
/// a richer record per session (<c>storages/session_projcache/sessions/session-&lt;id&gt;.json</c>):
/// the generated conversation title, the selected model, whether a turn is open, and any pending
/// <c>ask_user_question</c> prompts. That record is what tells a running task from a finished one.
/// Only files named <c>session-*.json</c> are user conversations; subagent sessions use plain ids.
/// </summary>
internal sealed class DeepSeekHarnessSessionReader
{
    private const int MaximumRecordBytes = 16 * 1024 * 1024;
    private const int ScanFileLimit = 96;
    private const int ParsedFileLimit = 512;
    private readonly string _sessionsDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _cacheWindow;
    private readonly object _sync = new();
    private readonly Dictionary<string, ParsedFile> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private List<AiThreadSummary>? _cached;
    private DateTimeOffset _cachedAt;

    public DeepSeekHarnessSessionReader(string? sessionsDirectory = null,
        Func<DateTimeOffset>? clock = null, TimeSpan? cacheWindow = null)
    {
        _sessionsDirectory = sessionsDirectory ?? DeepSeekHarnessPaths.GetSessionCacheDirectory();
        _clock = clock ?? (() => DateTimeOffset.Now);
        _cacheWindow = cacheWindow ?? TimeSpan.FromSeconds(1);
    }

    public string SessionsDirectory => _sessionsDirectory;

    public IReadOnlyList<AiThreadSummary> Read(int limit, CancellationToken cancellationToken = default)
    {
        var now = _clock();
        List<AiThreadSummary>? cached;
        lock (_sync)
        {
            cached = _cached is not null && now - _cachedAt < _cacheWindow ? _cached : null;
        }
        if (cached is not null) return Limit(cached, limit);

        var tasks = BuildTasks(cancellationToken);
        lock (_sync)
        {
            _cached = tasks;
            _cachedAt = now;
        }
        return Limit(tasks, limit);
    }

    private static IReadOnlyList<AiThreadSummary> Limit(List<AiThreadSummary> tasks, int limit) =>
        tasks.Count <= Math.Max(1, limit) ? tasks : tasks.Take(Math.Max(1, limit)).ToList();

    private List<AiThreadSummary> BuildTasks(CancellationToken cancellationToken)
    {
        var tasks = new List<AiThreadSummary>();
        FileInfo[] files;
        try
        {
            var directory = new DirectoryInfo(_sessionsDirectory);
            if (!directory.Exists) return tasks;
            // There is one record per session ever run; the newest checkpoints are the live tasks,
            // so scan a bounded window instead of folding the whole history on every refresh.
            files = directory.GetFiles("session-*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(ScanFileLimit)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return tasks;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.Name.StartsWith("session-", StringComparison.OrdinalIgnoreCase)) continue;
            seen.Add(file.FullName);
            if (TryReadCached(file) is { } task) tasks.Add(task);
        }
        Prune(seen);
        return AiManagerService.SortTasksByStatusAndTime(tasks).ToList();
    }

    /// <summary>Checkpoint files are replaced atomically; unchanged files are never re-parsed.</summary>
    private AiThreadSummary? TryReadCached(FileInfo file)
    {
        lock (_sync)
        {
            if (_parsed.TryGetValue(file.FullName, out var previous) &&
                previous.LastWriteUtc == file.LastWriteTimeUtc && previous.Length == file.Length)
                return previous.Task;
        }

        AiThreadSummary? task = null;
        if (file.Length is > 0 and <= MaximumRecordBytes)
        {
            try
            {
                task = Parse(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A partially written or newer-format record is simply not a usable task; the
                // LocalStorage fallback still names the conversation.
                task = null;
            }
        }

        lock (_sync)
        {
            _parsed[file.FullName] = new ParsedFile(file.LastWriteTimeUtc, file.Length, task);
        }
        return task;
    }

    private void Prune(HashSet<string> seen)
    {
        lock (_sync)
        {
            if (_parsed.Count <= ParsedFileLimit) return;
            foreach (var path in _parsed.Keys.Where(path => !seen.Contains(path)).ToList())
                _parsed.Remove(path);
        }
    }

    private AiThreadSummary? Parse(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object) return null;
        if (!record.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Object) return null;

        var title = ReadString(Row(rows, "title"));
        var turns = ReadPrompts(Row(rows, "turnOutline"));
        if (string.IsNullOrWhiteSpace(title))
            title = ReadString(Property(Property(Row(rows, "titleInput"), "first"), "text"));
        if (string.IsNullOrWhiteSpace(title) && turns.Count > 0) title = turns[0];
        if (string.IsNullOrWhiteSpace(title)) return null;

        var metadata = Row(rows, "sessionListMetadata");
        if (ReadBool(Property(metadata, "blank"))) return null;

        var latest = turns.Count > 0 ? turns[^1] : title;
        var questions = Property(Property(Row(rows, "userQuestions"), "questions"), "active");
        var waitingForInput = questions is { ValueKind: JsonValueKind.Array } active && active.GetArrayLength() > 0;
        var turnOpen = ReadBool(Property(Row(rows, "approvalState"), "turnOpen"));
        var stepOpen = Property(Row(rows, "sessionStats"), "openStep") is
            { ValueKind: JsonValueKind.Object };
        var status = waitingForInput ? "waitingOnUserInput"
            : turnOpen || stepOpen ? "inProgress"
            : "completed";

        return new AiThreadSummary
        {
            Id = Path.GetFileNameWithoutExtension(file.Name),
            Title = TaskTitleFormatter.Format(title, turns.FirstOrDefault()),
            ConversationTitle = title,
            LatestUserMessage = latest,
            Status = status,
            Source = AiProviderModes.DeepSeek,
            ModelName = ReadString(Property(Property(Row(rows, "modelSelection"), "lastUsed"), "model")),
            ProjectPath = ReadString(Property(Property(record, "identity"), "cwd")),
            OpenUri = DeepSeekDesktopApp.OpenUri,
            UpdatedAt = ReadUpdatedAt(rows) ?? new DateTimeOffset(file.LastWriteTimeUtc)
        };
    }

    private static DateTimeOffset? ReadUpdatedAt(JsonElement rows)
    {
        var messageAt = ReadLong(Property(Row(rows, "timeContext"), "lastMessageTime"));
        if (messageAt is > 0) return DateTimeOffset.FromUnixTimeMilliseconds(messageAt.Value);
        var promptAt = ReadLong(Property(Row(rows, "sessionListMetadata"), "lastPromptAt"));
        return promptAt is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(promptAt.Value) : null;
    }

    private static List<string> ReadPrompts(JsonElement? outline)
    {
        var prompts = new List<string>();
        if (Property(outline, "turns") is not { ValueKind: JsonValueKind.Array } turns) return prompts;
        foreach (var turn in turns.EnumerateArray())
        {
            var prompt = ReadString(Property(turn, "prompt"));
            if (!string.IsNullOrWhiteSpace(prompt)) prompts.Add(prompt.Trim());
        }
        return prompts;
    }

    /// <summary>Each projection row wraps its value as <c>{ "ver": n, "seq": n, "val": ... }</c>.</summary>
    private static JsonElement? Row(JsonElement rows, string name) =>
        Property(rows, name) is { ValueKind: JsonValueKind.Object } row &&
        row.TryGetProperty("val", out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child)
            ? child
            : null;

    private static string? ReadString(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool ReadBool(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.True };

    private static long? ReadLong(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number)
            ? number
            : null;

    private sealed record ParsedFile(DateTime LastWriteUtc, long Length, AiThreadSummary? Task);
}
