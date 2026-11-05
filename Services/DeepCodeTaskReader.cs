using System.Globalization;
using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

internal static class DeepCodePaths
{
    public static string GetHome()
    {
        var configured = Environment.GetEnvironmentVariable("DEEPCODE_HOME");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".deepcode")
            : configured;
    }

    public static string GetProjectsRoot(string? home = null) =>
        Path.Combine(string.IsNullOrWhiteSpace(home) ? GetHome() : home, "projects");

    public static string GetSettingsFile(string? home = null) =>
        Path.Combine(string.IsNullOrWhiteSpace(home) ? GetHome() : home, "settings.json");
}

internal sealed record DeepCodeSnapshot(
    IReadOnlyList<AiThreadSummary> Tasks,
    AiLocalUsageSummary LocalUsage);

/// <summary>
/// Reads Deep Code (DeepSeek's official coding CLI) project session indexes. The CLI keeps one
/// sessions-index.json per project, containing titles, statuses, token usage, and the project root.
/// Only local files are read; nothing is started or written.
/// </summary>
internal sealed class DeepCodeTaskReader
{
    private readonly string _projectsRoot;
    private readonly string _settingsFile;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _cacheWindow;
    private readonly object _sync = new();
    private DeepCodeSnapshot? _cached;
    private DateTimeOffset _cachedAt;
    private DateOnly _cachedWeekStart;
    private SettingsCache? _settingsCache;

    public DeepCodeTaskReader(string? projectsRoot = null, string? settingsFile = null,
        Func<DateTimeOffset>? clock = null, TimeSpan? cacheWindow = null)
    {
        _projectsRoot = projectsRoot ?? DeepCodePaths.GetProjectsRoot();
        _settingsFile = settingsFile ?? DeepCodePaths.GetSettingsFile();
        _clock = clock ?? (() => DateTimeOffset.Now);
        _cacheWindow = cacheWindow ?? TimeSpan.FromSeconds(1);
    }

    public string ProjectsRoot => _projectsRoot;

    public DeepCodeSnapshot Read(int taskLimit, DateOnly weekStart,
        CancellationToken cancellationToken = default)
    {
        var now = _clock();
        lock (_sync)
        {
            if (_cached is not null && _cachedWeekStart == weekStart &&
                now - _cachedAt < _cacheWindow) return _cached;
        }

        var settings = ReadSettings();
        var entries = ReadEntries(cancellationToken);
        var tasks = BuildTasks(entries, settings, taskLimit);
        var localUsage = BuildLocalUsage(entries, settings, weekStart, now);
        var snapshot = new DeepCodeSnapshot(tasks, localUsage);
        lock (_sync)
        {
            _cached = snapshot;
            _cachedAt = now;
            _cachedWeekStart = weekStart;
        }
        return snapshot;
    }

    public IReadOnlyList<AiThreadSummary> ReadTasks(int limit, CancellationToken cancellationToken = default) =>
        BuildTasks(ReadEntries(cancellationToken), ReadSettings(), limit);

    public AiLocalUsageSummary ReadLocalUsage(DateOnly weekStart,
        CancellationToken cancellationToken = default)
    {
        return BuildLocalUsage(ReadEntries(cancellationToken), ReadSettings(), weekStart, _clock());
    }

    private List<DeepCodeEntry> ReadEntries(CancellationToken cancellationToken)
    {
        var result = new List<DeepCodeEntry>();
        if (!Directory.Exists(_projectsRoot)) return result;

        string[] projectDirectories;
        try
        {
            projectDirectories = Directory.GetDirectories(_projectsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var projectDirectory in projectDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(projectDirectory, "sessions-index.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 128 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var projectPath = ReadString(root, "originalPath") ?? Path.GetFileName(projectDirectory);
                if (!TryReadProperty(root, "entries", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in entries.EnumerateArray())
                    if (ParseEntry(entry, projectPath) is { } parsed) result.Add(parsed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Debug.WriteLine($"[DeepCode] Session index unavailable: {ex.Message}");
            }
        }
        return result;
    }

    private static DeepCodeEntry? ParseEntry(JsonElement entry, string projectPath)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        var id = ReadString(entry, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var summary = ReadString(entry, "summary");
        var status = ReadString(entry, "status") ?? "completed";
        var updated = TryReadTime(ReadString(entry, "updateTime")) ?? TryReadTime(ReadString(entry, "createTime"));
        long tokens = 0;
        var modelTokens = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (TryReadProperty(entry, "usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            tokens = ReadLong(usage, "total_tokens");
        if (TryReadProperty(entry, "usagePerModel", out var perModel) && perModel.ValueKind == JsonValueKind.Object)
        {
            foreach (var model in perModel.EnumerateObject())
            {
                if (model.Value.ValueKind != JsonValueKind.Object ||
                    string.IsNullOrWhiteSpace(model.Name)) continue;
                var value = ReadLong(model.Value, "total_tokens");
                if (value > 0) modelTokens[model.Name.Trim()] = value;
            }
        }
        tokens = Math.Max(tokens, modelTokens.Values.Sum());
        return new DeepCodeEntry(id.Trim(), CleanSummary(summary), status, tokens, modelTokens,
            updated, projectPath);
    }

    private static IReadOnlyList<AiThreadSummary> BuildTasks(List<DeepCodeEntry> entries,
        DeepCodeSettings settings, int limit)
    {
        var tasks = new Dictionary<string, AiThreadSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var model = entry.ModelTokens.Count > 0
                ? entry.ModelTokens.OrderByDescending(item => item.Value)
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase).First().Key
                : settings.Model;
            tasks[entry.Id] = new AiThreadSummary
            {
                Id = entry.Id,
                Title = entry.Summary ?? "未命名任务",
                ConversationTitle = entry.Summary,
                Status = MapStatus(entry.Status),
                ModelName = model,
                ReasoningEffort = settings.ReasoningEffort,
                ProjectPath = entry.ProjectPath,
                UpdatedAt = entry.UpdatedAt
            };
        }
        return AiManagerService.SortTasksByStatusAndTime(tasks.Values)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    private static AiLocalUsageSummary BuildLocalUsage(List<DeepCodeEntry> entries,
        DeepCodeSettings settings, DateOnly weekStart, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var weekEnd = weekStart.AddDays(6);
        var rollingStart = today.AddDays(-6);
        var scanStart = rollingStart < weekStart ? rollingStart : weekStart;
        var records = entries
            .Where(entry => entry.UpdatedAt is not null)
            .Select(entry => (Entry: entry, Date: DateOnly.FromDateTime(entry.UpdatedAt!.Value.LocalDateTime)))
            .Where(item => item.Date >= scanStart && item.Date <= weekEnd)
            .ToList();
        var weekly = records.Where(item => item.Date >= weekStart && item.Date <= weekEnd).ToList();
        var total = weekly.Sum(item => item.Entry.Tokens);
        var todayTokens = weekly.Where(item => item.Date == today).Sum(item => item.Entry.Tokens);

        var projects = weekly
            .GroupBy(item => item.Entry.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AiProjectUsage
            {
                ProjectName = ProjectDisplayName(group.Key),
                ProjectPath = group.Key,
                Tokens = group.Sum(item => item.Entry.Tokens),
                SharePercent = total > 0 ? group.Sum(item => item.Entry.Tokens) * 100d / total : 0,
                SessionCount = group.Select(item => item.Entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                MostUsedModel = MostUsedModel(group.SelectMany(item => item.Entry.ModelTokens))
                                ?? settings.Model
            })
            .OrderByDescending(item => item.Tokens)
            .ThenBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new AiLocalUsageSummary
        {
            CountedFrom = records.Count == 0
                ? null
                : new DateTimeOffset(records.Min(item => item.Date).ToDateTime(TimeOnly.MinValue)),
            PeriodStart = weekStart,
            PeriodEnd = weekEnd,
            TotalTokens = total,
            TodayTokens = todayTokens,
            SessionCount = weekly.Select(item => item.Entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            MostUsedModel = MostUsedModel(weekly.SelectMany(item => item.Entry.ModelTokens)) ?? settings.Model,
            DailyUsage = Enumerable.Range(0, 7)
                .Select(offset => rollingStart.AddDays(offset))
                .Select(date => new AiDailyUsage
                {
                    Date = date,
                    Tokens = records.Where(item => item.Date == date).Sum(item => item.Entry.Tokens)
                })
                .ToList(),
            TodayHourlyUsage = Enumerable.Range(0, 24)
                .Select(hour => new AiHourlyUsage { Hour = hour, Tokens = 0 })
                .ToList(),
            Projects = projects
        };
    }

    private static string? MostUsedModel(IEnumerable<KeyValuePair<string, long>> models) => models
        .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
        .Select(group => new { Name = group.Key, Tokens = group.Sum(item => item.Value) })
        .Where(item => item.Tokens > 0)
        .OrderByDescending(item => item.Tokens)
        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .Select(item => item.Name)
        .FirstOrDefault();

    private static string ProjectDisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return LocalizationService.L("未归属项目", "Unassigned");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path.Trim()));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string? CleanSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var cleaned = TaskTitleFormatter.Clean(summary);
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    internal static string MapStatus(string status) => status.Trim().ToLowerInvariant() switch
    {
        "processing" => "inProgress",
        "ask_permission" => "waitingOnApproval",
        "waiting_for_user" => "waitingOnUserInput",
        "pending" => "pending",
        "failed" or "permission_denied" => "failed",
        "interrupted" => "interrupted",
        "completed" => "completed",
        _ => "notLoaded"
    };

    private DeepCodeSettings ReadSettings()
    {
        try
        {
            var info = new FileInfo(_settingsFile);
            if (!info.Exists)
            {
                _settingsCache = null;
                return default;
            }
            var cached = _settingsCache;
            if (cached is not null && cached.LastWriteUtc == info.LastWriteTimeUtc)
                return cached.Settings;
            using var document = JsonDocument.Parse(File.ReadAllText(_settingsFile));
            var root = document.RootElement;
            string? model = null;
            string? effort = ReadString(root, "reasoningEffort");
            if (TryReadProperty(root, "env", out var env) && env.ValueKind == JsonValueKind.Object)
                model = ReadString(env, "MODEL") ?? ReadString(env, "model");
            model ??= ReadString(root, "model");
            var settings = new DeepCodeSettings(model, effort);
            _settingsCache = new SettingsCache(info.LastWriteTimeUtc, settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    private static bool TryReadProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryReadProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long ReadLong(JsonElement element, string name)
    {
        if (!TryReadProperty(element, name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return 0;
    }

    private static DateTimeOffset? TryReadTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var value)
            ? value
            : null;

    private readonly record struct DeepCodeSettings(string? Model, string? ReasoningEffort);

    private sealed record SettingsCache(DateTime LastWriteUtc, DeepCodeSettings Settings);

    private sealed record DeepCodeEntry(string Id, string? Summary, string Status, long Tokens,
        Dictionary<string, long> ModelTokens, DateTimeOffset? UpdatedAt, string ProjectPath);
}
