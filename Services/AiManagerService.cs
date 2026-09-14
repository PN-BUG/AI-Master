using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIMaster.Models;

namespace AIMaster.Services;

internal sealed class AiManagerService : IAsyncDisposable
{
    private readonly CodexAppServerClient _client = new();
    private readonly string _settingsPath;
    private readonly ConcurrentDictionary<string, RolloutCursor> _rolloutCursors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LocalUsageCursor> _localUsageCursors =
        new(StringComparer.OrdinalIgnoreCase);
    private List<AiLimitWindow> _floatingLimits = new();
    private DateTimeOffset _floatingLimitsAt;
    private JsonElement? _floatingModelsRoot;
    private DateTimeOffset _floatingModelsAt;

    private sealed class RolloutCursor
    {
        public readonly object Sync = new();
        public readonly MemoryStream PendingLine = new();
        public long Offset;
        public string? Status;
        public string? ThreadId;
        public string? FirstUserMessage;
        public string? LatestUserMessage;
        public string? ModelName;
        public string? ReasoningEffort;
        public string? ThreadSource;
    }

    private sealed class LocalUsageCursor
    {
        public readonly object Sync = new();
        public readonly MemoryStream PendingLine = new();
        public readonly List<LocalUsageRecord> Records = new();
        public long Offset;
        public string? ProjectPath;
    }

    private sealed record LocalUsageRecord(DateOnly Date, string ProjectPath, long Tokens);

    public AiManagerService()
    {
        _settingsPath = AppPaths.GetSettingsFile();
        Settings = LoadSettings();
    }

    public AiManagerSettings Settings { get; private set; }
    public AiManagerSnapshot? LastSnapshot { get; private set; }

    public async Task<AiManagerSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _client.ConnectAsync(cancellationToken);
        // These requests are independent. Starting them together removes a full optional-request
        // round trip from first paint while the local JSONL work stays off the UI thread.
        var rateTask = _client.RequestAsync("account/rateLimits/read",
            cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(45));
        var usageTask = TryRequestAsync("account/usage/read", null, TimeSpan.FromSeconds(12), cancellationToken);
        var threadsTask = TryRequestAsync("thread/list", BuildThreadListParameters(), TimeSpan.FromSeconds(12), cancellationToken);
        var localDataTask = Task.Run(() =>
        {
            var tasks = DiscoverLocalTasks(48, 12, cancellationToken);
            var usage = DiscoverLocalUsage(cancellationToken);
            return (Tasks: tasks, Usage: usage);
        }, cancellationToken);

        var rate = await rateTask;
        var usage = await usageTask;
        var threads = await threadsTask;
        var localData = await localDataTask;

        var serverThreads = threads is { } threadValue ? ParseThreads(threadValue) : new();
        var mergedThreads = MergeFloatingTasks(serverThreads, localData.Tasks)
            .Where(IsUsefulFloatingTask)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(12)
            .ToList();
        LastSnapshot = new AiManagerSnapshot
        {
            Limits = ParseLimits(rate),
            DailyUsage = usage is { } usageValue ? ParseDailyUsage(usageValue) : new(),
            LifetimeTokens = usage is { } usageSummary ? ParseLifetimeTokens(usageSummary) : null,
            Threads = mergedThreads,
            ResetCredits = ParseResetCredits(rate),
            LocalUsage = localData.Usage,
            CapturedAt = DateTimeOffset.Now
        };
        return LastSnapshot;
    }

    public async Task<AiFloatingSnapshot> RefreshFloatingAsync(CancellationToken cancellationToken = default)
    {
        var appServerAvailable = await TryConnectFloatingAsync(cancellationToken);
        var now = DateTimeOffset.Now;
        var refreshLimits = _floatingLimits.Count == 0 || now - _floatingLimitsAt >= TimeSpan.FromMinutes(1);
        var refreshModels = _floatingModelsRoot is null || now - _floatingModelsAt >= TimeSpan.FromMinutes(10);

        // Start from the fast state database so a cold floating-window launch does not wait for
        // a full rollout scan. Each visible task is enriched below from thread/read and its rollout.
        var localTasksTask = Task.Run(() => DiscoverLocalTasks(24, 8, cancellationToken), cancellationToken);
        var threadsTask = appServerAvailable
            ? TryRequestAsync("thread/list", BuildLatestThreadParameters(), TimeSpan.FromSeconds(10), cancellationToken)
            : Task.FromResult<JsonElement?>(null);
        var rateTask = appServerAvailable && refreshLimits
            ? TryRequestAsync("account/rateLimits/read", null, TimeSpan.FromSeconds(8), cancellationToken)
            : Task.FromResult<JsonElement?>(null);
        var modelsTask = appServerAvailable && refreshModels
            ? TryRequestAsync("model/list", new JsonObject
            {
                ["limit"] = 100,
                ["includeHidden"] = true
            }, TimeSpan.FromSeconds(8), cancellationToken)
            : Task.FromResult<JsonElement?>(null);
        var threadsRoot = await threadsTask;
        var rate = await rateTask;
        var modelsRoot = await modelsTask;
        if (rate is { } rateValue)
        {
            _floatingLimits = ParseLimits(rateValue);
            _floatingLimitsAt = now;
        }
        if (modelsRoot is { } modelsValue)
        {
            _floatingModelsRoot = modelsValue.Clone();
            _floatingModelsAt = now;
        }

        var threads = threadsRoot is { } threadValue ? ParseThreads(threadValue) : new();
        var serverCandidates = threads.Where(IsUsefulFloatingTask)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(8)
            .ToList();
        var enrichedServerTasks = serverCandidates.Count == 0
            ? new List<AiThreadSummary>()
            : (await Task.WhenAll(serverCandidates.Select(item => EnrichFloatingTaskAsync(item, cancellationToken))))
                .ToList();

        // A packaged app-server can temporarily return an empty state database or `notLoaded`
        // for sessions owned by another Codex process. Rollout JSONL files are shared persisted
        // state, so merge them in as an independent source instead of showing a false empty state.
        var localTasks = await localTasksTask;
        var visibleTasks = MergeFloatingTasks(enrichedServerTasks, localTasks)
            .Where(IsUsefulFloatingTask)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(3)
            .ToList();
        var focus = visibleTasks.FirstOrDefault();

        var model = ResolveModel(_floatingModelsRoot, focus?.ModelName);
        var limits = _floatingLimits;
        var tightest = limits.OrderBy(item => item.RemainingPercent).FirstOrDefault();
        return new AiFloatingSnapshot
        {
            Tasks = visibleTasks,
            TaskName = focus?.DisplayTitle ?? LocalizationService.L("暂无最近任务", "No recent tasks"),
            TaskStatus = focus?.Status ?? "notLoaded",
            TaskStatusText = focus?.StatusText ?? LocalizationService.L("等待任务", "Waiting for a task"),
            ModelName = model.DisplayName ?? focus?.ModelName ?? LocalizationService.L("模型未知", "Unknown model"),
            ReasoningEffort = focus?.ReasoningEffort ?? model.ReasoningEffort,
            RemainingPercent = tightest?.RemainingPercent,
            QuotaWindow = tightest is null
                ? LocalizationService.L("额度窗口未知", "Unknown quota window")
                : $"{tightest.DisplayName} · {tightest.WindowName}",
            ResetsAt = tightest?.ResetsAt,
            CapturedAt = DateTimeOffset.Now
        };
    }

    private async Task<bool> TryConnectFloatingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.ConnectAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[AiManager] App-server unavailable; using local sessions: {ex.Message}");
            return false;
        }
    }

    private static bool IsUsefulFloatingTask(AiThreadSummary thread) =>
        !string.IsNullOrWhiteSpace(thread.Id) &&
        !thread.Title.StartsWith("The following is the Codex agent history", StringComparison.OrdinalIgnoreCase);

    private List<AiThreadSummary> DiscoverLocalTasks(int scanLimit, int resultLimit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var codexHome = string.IsNullOrWhiteSpace(configuredHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : configuredHome;
            var sessionsRoot = Path.Combine(codexHome, "sessions");
            if (!Directory.Exists(sessionsRoot)) return new();

            var candidates = Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new { Path = path, UpdatedAt = File.GetLastWriteTimeUtc(path) })
                .OrderByDescending(item => item.UpdatedAt)
                .Take(scanLimit)
                .ToList();
            var tasks = new Dictionary<string, AiThreadSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var task = ReadLocalTask(item.Path, item.UpdatedAt);
                if (task is null || !IsUsefulFloatingTask(task) || tasks.ContainsKey(task.Id)) continue;
                tasks[task.Id] = task;
                if (tasks.Count >= resultLimit) break;
            }
            return tasks.Values
                .OrderByDescending(item => item.UpdatedAt)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[AiManager] Local sessions unavailable: {ex.Message}");
            return new();
        }
    }

    internal AiThreadSummary? ReadLocalTask(string path, DateTime updatedAtUtc)
    {
        // Reading status also incrementally parses the small set of metadata needed by the
        // floating window. Existing files are scanned once; later refreshes only read appends.
        var status = ReadRolloutExecutionStatus(path, "completed");
        if (!_rolloutCursors.TryGetValue(path, out var cursor)) return null;
        lock (cursor.Sync)
        {
            if (string.IsNullOrWhiteSpace(cursor.ThreadId) ||
                (!string.IsNullOrWhiteSpace(cursor.ThreadSource) &&
                 !string.Equals(cursor.ThreadSource, "user", StringComparison.OrdinalIgnoreCase)))
                return null;
            return new AiThreadSummary
            {
                Id = cursor.ThreadId,
                Title = SelectTaskTitle(cursor.FirstUserMessage, cursor.LatestUserMessage, "未命名任务"),
                ConversationTitle = cursor.FirstUserMessage,
                LatestUserMessage = cursor.LatestUserMessage,
                Status = string.Equals(status, "notLoaded", StringComparison.OrdinalIgnoreCase)
                    ? "completed"
                    : status,
                ModelName = cursor.ModelName,
                ReasoningEffort = cursor.ReasoningEffort,
                RolloutPath = path,
                UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc))
            };
        }
    }

    private IEnumerable<AiThreadSummary> MergeFloatingTasks(
        IEnumerable<AiThreadSummary> serverTasks,
        IEnumerable<AiThreadSummary> localTasks)
    {
        var merged = new Dictionary<string, AiThreadSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in serverTasks) merged[item.Id] = ApplyTaskNamePreference(item);
        foreach (var local in localTasks)
        {
            if (!merged.TryGetValue(local.Id, out var server))
            {
                merged[local.Id] = ApplyTaskNamePreference(local);
                continue;
            }

            var conversationTitle = IsUsefulTitle(server.ConversationTitle)
                ? server.ConversationTitle
                : local.ConversationTitle;
            var latestUserMessage = IsUsefulTitle(local.LatestUserMessage)
                ? local.LatestUserMessage
                : server.LatestUserMessage;
            merged[local.Id] = new AiThreadSummary
            {
                Id = local.Id,
                Title = SelectTaskTitle(conversationTitle, latestUserMessage, server.Title),
                ConversationTitle = conversationTitle,
                LatestUserMessage = latestUserMessage,
                Status = local.Status,
                ModelName = local.ModelName ?? server.ModelName,
                ReasoningEffort = local.ReasoningEffort ?? server.ReasoningEffort,
                RolloutPath = local.RolloutPath ?? server.RolloutPath,
                UpdatedAt = local.UpdatedAt ?? server.UpdatedAt
            };
        }
        return merged.Values;
    }

    private AiThreadSummary ApplyTaskNamePreference(AiThreadSummary item) => new()
    {
        Id = item.Id,
        Title = SelectTaskTitle(item.ConversationTitle, item.LatestUserMessage, item.Title),
        ConversationTitle = item.ConversationTitle,
        LatestUserMessage = item.LatestUserMessage,
        Status = item.Status,
        ModelName = item.ModelName,
        ReasoningEffort = item.ReasoningEffort,
        RolloutPath = item.RolloutPath,
        UpdatedAt = item.UpdatedAt
    };

    private string SelectTaskTitle(string? conversationTitle, string? latestUserMessage, string? fallback)
    {
        var source = TaskNameSources.Normalize(Settings.TaskNameSource);
        var preferred = source == TaskNameSources.LatestUserMessage
            ? latestUserMessage
            : conversationTitle;
        var alternate = source == TaskNameSources.LatestUserMessage
            ? conversationTitle
            : latestUserMessage;
        return NormalizeTitle(FirstUsefulTitle(preferred, alternate, fallback) ?? "未命名任务");
    }

    private static string? FirstUsefulTitle(params string?[] values) => values.FirstOrDefault(IsUsefulTitle);

    private static bool IsUsefulTitle(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value != "未命名任务" &&
        !value.StartsWith("The following is the Codex agent history", StringComparison.OrdinalIgnoreCase);

    private async Task<AiThreadSummary> EnrichFloatingTaskAsync(AiThreadSummary summary,
        CancellationToken cancellationToken)
    {
        var status = ReadRolloutExecutionStatus(summary.RolloutPath, summary.Status);
        if (!string.IsNullOrWhiteSpace(summary.RolloutPath) && !string.IsNullOrWhiteSpace(summary.ModelName))
            return new AiThreadSummary
            {
                Id = summary.Id,
                Title = summary.Title,
                ConversationTitle = summary.ConversationTitle,
                LatestUserMessage = summary.LatestUserMessage,
                Status = status,
                ModelName = summary.ModelName,
                ReasoningEffort = summary.ReasoningEffort,
                RolloutPath = summary.RolloutPath,
                UpdatedAt = summary.UpdatedAt
            };
        return await EnrichFromLatestTurnAsync(summary, cancellationToken);
    }

    private async Task<JsonElement?> TryRequestAsync(string method, JsonNode? parameters, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try { return await _client.RequestAsync(method, parameters, cancellationToken, timeout); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[AiManager] Optional request {method} failed: {ex.Message}");
            return null;
        }
    }

    public void SaveSettings(AiManagerSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
        Settings = settings;
    }

    public AiManagerSettings ReloadSettings()
    {
        Settings = LoadSettings();
        return Settings;
    }

    public AiForecast BuildForecast(AiManagerSnapshot snapshot)
    {
        var recent = snapshot.DailyUsage
            .Where(item => item.Date >= DateOnly.FromDateTime(DateTime.Today.AddDays(-6)))
            .ToList();
        var dailyTokens = recent.Sum(item => (double)item.Tokens) / 7d;
        var main = snapshot.MainLimit;
        if (main?.ResetsAt is null || main.WindowDurationMinutes <= 0 || main.UsedPercent <= 0)
            return new AiForecast(dailyTokens, 0, null, false,
                LocalizationService.L("积累更多用量后可预测耗尽时间", "More usage data is needed before forecasting exhaustion"));

        var start = main.ResetsAt.Value.AddMinutes(-main.WindowDurationMinutes);
        var elapsedDays = Math.Max((snapshot.CapturedAt - start).TotalDays, 1d / 24d);
        var dailyPercent = main.UsedPercent / elapsedDays;
        if (dailyPercent <= 0.001)
            return new AiForecast(dailyTokens, dailyPercent, null, false,
                LocalizationService.L("当前消耗很低，本周期预计不会耗尽", "Current usage is low; this quota window is unlikely to be exhausted"));

        var exhaustion = snapshot.CapturedAt.AddDays(main.RemainingPercent / dailyPercent);
        var beforeReset = exhaustion < main.ResetsAt.Value;
        var projectedPercent = Math.Min(999,
            main.UsedPercent + dailyPercent * (main.ResetsAt.Value - snapshot.CapturedAt).TotalDays);
        var summary = beforeReset
            ? LocalizationService.IsEnglish
                ? $"At the current rate, the quota may be exhausted by {exhaustion.LocalDateTime.ToString("MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
                : $"按当前速度，预计 {exhaustion.LocalDateTime:M月d日 HH:mm} 触顶"
            : LocalizationService.IsEnglish
                ? $"At the current rate, usage may reach about {projectedPercent:0}% by the end of this window"
                : $"按当前速度，本周期结束时约使用 {projectedPercent:0}%";
        return new AiForecast(dailyTokens, dailyPercent, exhaustion, beforeReset, summary);
    }

    private AiLocalUsageSummary DiscoverLocalUsage(CancellationToken cancellationToken)
    {
        try
        {
            var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var codexHome = string.IsNullOrWhiteSpace(configuredHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : configuredHome;
            var sessionsRoot = Path.Combine(codexHome, "sessions");
            var today = DateOnly.FromDateTime(DateTime.Now);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            return ReadLocalUsageSummary(sessionsRoot, monday, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[AiManager] Local usage unavailable: {ex.Message}");
            return new AiLocalUsageSummary();
        }
    }

    internal AiLocalUsageSummary ReadLocalUsageSummary(string sessionsRoot, DateOnly weekStart,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sessionsRoot)) return new AiLocalUsageSummary();
        var weekEnd = weekStart.AddDays(6);
        var earliestWriteUtc = weekStart.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var sessionRecords = new List<(string SessionPath, LocalUsageRecord Record)>();

        foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updatedAt = File.GetLastWriteTimeUtc(path);
            if (updatedAt < earliestWriteUtc) continue;
            ScanLocalUsageFile(path, DateOnly.FromDateTime(updatedAt.ToLocalTime()), cancellationToken);
            if (!_localUsageCursors.TryGetValue(path, out var cursor)) continue;
            lock (cursor.Sync)
            {
                sessionRecords.AddRange(cursor.Records
                    .Where(item => item.Date >= weekStart && item.Date <= weekEnd)
                    .Select(item => (path, item)));
            }
        }

        var total = sessionRecords.Sum(item => item.Record.Tokens);
        var projects = sessionRecords
            .GroupBy(item => item.Record.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AiProjectUsage
            {
                ProjectName = ProjectDisplayName(group.Key),
                ProjectPath = group.Key,
                Tokens = group.Sum(item => item.Record.Tokens),
                SharePercent = total > 0 ? group.Sum(item => item.Record.Tokens) * 100d / total : 0,
                SessionCount = group.Select(item => item.SessionPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            })
            .OrderByDescending(item => item.Tokens)
            .ThenBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new AiLocalUsageSummary
        {
            TotalTokens = total,
            SessionCount = sessionRecords.Select(item => item.SessionPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Projects = projects
        };
    }

    private void ScanLocalUsageFile(string path, DateOnly fallbackDate, CancellationToken cancellationToken)
    {
        var cursor = _localUsageCursors.GetOrAdd(path, _ => new LocalUsageCursor());
        lock (cursor.Sync)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length < cursor.Offset)
            {
                cursor.Offset = 0;
                cursor.ProjectPath = null;
                cursor.Records.Clear();
                cursor.PendingLine.SetLength(0);
            }
            stream.Position = cursor.Offset;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segmentStart = 0;
                for (var index = 0; index < read; index++)
                {
                    if (buffer[index] != (byte)'\n') continue;
                    cursor.PendingLine.Write(buffer, segmentStart, index - segmentStart);
                    ParseLocalUsageLine(cursor, fallbackDate);
                    cursor.PendingLine.SetLength(0);
                    segmentStart = index + 1;
                }
                if (segmentStart < read)
                    cursor.PendingLine.Write(buffer, segmentStart, read - segmentStart);
                cursor.Offset += read;
            }
        }
    }

    private static void ParseLocalUsageLine(LocalUsageCursor cursor, DateOnly fallbackDate)
    {
        if (cursor.PendingLine.Length == 0) return;
        var line = Encoding.UTF8.GetString(cursor.PendingLine.GetBuffer(), 0, (int)cursor.PendingLine.Length);
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload)) return;
            var type = ReadString(root, "type");
            if (type is "session_meta" or "turn_context")
            {
                cursor.ProjectPath = NormalizeProjectPath(ReadString(payload, "cwd")) ?? cursor.ProjectPath;
                return;
            }
            if (type != "token_usage_record" ||
                !payload.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return;
            var tokens = ReadLong(usage, "total_tokens");
            if (tokens <= 0) return;
            var date = DateTimeOffset.TryParse(ReadString(root, "timestamp"), out var timestamp)
                ? DateOnly.FromDateTime(timestamp.LocalDateTime)
                : fallbackDate;
            cursor.Records.Add(new LocalUsageRecord(date,
                cursor.ProjectPath ?? LocalizationService.L("未归属项目", "Unassigned"), tokens));
        }
        catch (JsonException)
        {
            // An incomplete or malformed record does not invalidate the rest of the session.
        }
    }

    private static string? NormalizeProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(path.Trim());
    }

    private static string ProjectDisplayName(string path)
    {
        if (path == "未归属项目" || path == "Unassigned")
            return LocalizationService.L("未归属项目", "Unassigned");
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    internal static IReadOnlyList<AiRemainingForecastPoint> BuildWeeklyRemainingForecast(
        AiManagerSnapshot snapshot, AiForecast forecast)
    {
        var main = snapshot.MainLimit;
        if (main is null) return Array.Empty<AiRemainingForecastPoint>();

        var today = DateOnly.FromDateTime(snapshot.CapturedAt.LocalDateTime);
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-daysFromMonday);
        return Enumerable.Range(0, 7)
            .Select(index =>
            {
                var date = monday.AddDays(index);
                var distanceFromToday = index - daysFromMonday;
                var remaining = Math.Clamp(
                    main.RemainingPercent - forecast.DailyPercent * distanceFromToday, 0, 100);
                return new AiRemainingForecastPoint(date, remaining, date > today);
            })
            .ToList();
    }

    public async Task<int> PauseActiveThreadsAsync(CancellationToken cancellationToken = default)
    {
        if (!_client.IsConnected) await _client.ConnectAsync(cancellationToken);
        var list = await _client.RequestAsync("thread/list", BuildThreadListParameters(), cancellationToken);
        var active = ParseThreads(list).Where(item => item.IsActive).ToList();
        var interrupted = 0;

        foreach (var thread in active)
        {
            try
            {
                var detail = await _client.RequestAsync("thread/read", new JsonObject
                {
                    ["threadId"] = thread.Id,
                    ["includeTurns"] = true
                }, cancellationToken);
                if (!detail.TryGetProperty("thread", out var threadElement) ||
                    !threadElement.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
                    continue;

                string? turnId = null;
                foreach (var turn in turns.EnumerateArray().Reverse())
                {
                    if (!turn.TryGetProperty("status", out var status) ||
                        !string.Equals(status.GetString(), "inProgress", StringComparison.OrdinalIgnoreCase)) continue;
                    if (turn.TryGetProperty("id", out var id)) turnId = id.GetString();
                    break;
                }
                if (string.IsNullOrWhiteSpace(turnId)) continue;
                await _client.RequestAsync("turn/interrupt", new JsonObject
                {
                    ["threadId"] = thread.Id,
                    ["turnId"] = turnId
                }, cancellationToken);
                interrupted++;
            }
            catch (InvalidOperationException)
            {
                // The task may have completed between discovery and interruption.
            }
        }
        return interrupted;
    }

    private AiManagerSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new AiManagerSettings();
            var settings = JsonSerializer.Deserialize<AiManagerSettings>(File.ReadAllText(_settingsPath))
                           ?? new AiManagerSettings();
            settings.TaskNameSource = TaskNameSources.Normalize(settings.TaskNameSource);
            settings.FloatingFontScale = FloatingFontScales.Normalize(settings.FloatingFontScale);
            settings.DashboardCardLayout ??= new List<string>();
            settings.CollapsedDashboardCards ??= new List<string>();
            settings.DashboardCardSizes = settings.DashboardCardSizes is null
                ? new Dictionary<string, AiDashboardCardSize>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, AiDashboardCardSize>(settings.DashboardCardSizes,
                    StringComparer.OrdinalIgnoreCase);
            return settings;
        }
        catch
        {
            return new AiManagerSettings();
        }
    }

    private static JsonObject BuildThreadListParameters(int limit = 12, bool interactiveOnly = false)
    {
        var sources = new JsonArray();
        var sourceKinds = interactiveOnly
            ? new[] { "cli", "vscode", "exec", "appServer" }
            : new[]
            {
                "cli", "vscode", "exec", "appServer", "subAgent", "subAgentReview",
                "subAgentCompact", "subAgentThreadSpawn", "subAgentOther", "unknown"
            };
        foreach (var value in sourceKinds)
            sources.Add(value);
        return new JsonObject
        {
            ["limit"] = limit,
            ["sortKey"] = "updated_at",
            ["sortDirection"] = "desc",
            ["sourceKinds"] = sources,
            ["useStateDbOnly"] = true
        };
    }

    private static JsonObject BuildLatestThreadParameters() => new()
    {
        ["limit"] = 8,
        ["sortKey"] = "updated_at",
        ["sortDirection"] = "desc",
        ["useStateDbOnly"] = true
    };

    private static List<AiLimitWindow> ParseLimits(JsonElement root)
    {
        var result = new List<AiLimitWindow>();
        if (root.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in buckets.EnumerateObject()) ParseBucket(bucket.Name, bucket.Value, result);
        }
        else if (root.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            var id = ReadString(single, "limitId") ?? "codex";
            ParseBucket(id, single, result);
        }
        return result.OrderByDescending(item => item.WindowDurationMinutes).ThenBy(item => item.Name).ToList();
    }

    private static void ParseBucket(string id, JsonElement bucket, ICollection<AiLimitWindow> output)
    {
        var name = ReadString(bucket, "limitName");
        if (string.IsNullOrWhiteSpace(name)) name = id.Equals("codex", StringComparison.OrdinalIgnoreCase) ? "Codex 总额度" : id;
        AddWindow(bucket, "primary", id, name, true, output);
        AddWindow(bucket, "secondary", id, name, false, output);
    }

    private static void AddWindow(JsonElement bucket, string property, string id, string name, bool isPrimary,
        ICollection<AiLimitWindow> output)
    {
        if (!bucket.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object) return;
        var duration = ReadInt(window, "windowDurationMins");
        var resetSeconds = ReadLong(window, "resetsAt");
        output.Add(new AiLimitWindow
        {
            LimitId = id,
            Name = name,
            UsedPercent = Math.Clamp(ReadDouble(window, "usedPercent"), 0, 100),
            WindowDurationMinutes = duration,
            ResetsAt = resetSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetSeconds) : null,
            IsPrimary = isPrimary
        });
    }

    private static List<AiDailyUsage> ParseDailyUsage(JsonElement root)
    {
        var result = new List<AiDailyUsage>();
        if (!root.TryGetProperty("dailyUsageBuckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in buckets.EnumerateArray())
        {
            if (!item.TryGetProperty("startDate", out var dateElement) ||
                !DateOnly.TryParse(dateElement.GetString(), out var date)) continue;
            result.Add(new AiDailyUsage { Date = date, Tokens = ReadLong(item, "tokens") });
        }
        return result.OrderBy(item => item.Date).ToList();
    }

    private static long? ParseLifetimeTokens(JsonElement root)
    {
        if (!root.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object ||
            !summary.TryGetProperty("lifetimeTokens", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return JsonProtocolValue.TryGetInt64(value, out var tokens) ? tokens : null;
    }

    private static int ParseResetCredits(JsonElement root)
    {
        if (!root.TryGetProperty("rateLimitResetCredits", out var credits) || credits.ValueKind != JsonValueKind.Object)
            return 0;
        return ReadInt(credits, "availableCount");
    }

    private static List<AiThreadSummary> ParseThreads(JsonElement root)
    {
        var result = new List<AiThreadSummary>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in data.EnumerateArray())
        {
            var conversationTitle = ReadString(item, "name") ?? ReadString(item, "title");
            var latestUserMessage = ReadString(item, "preview");
            var title = NormalizeTitle(conversationTitle ?? latestUserMessage ?? "未命名任务");
            var status = item.TryGetProperty("status", out var statusElement)
                ? ParseThreadStatus(statusElement)
                : "notLoaded";
            var updated = ReadLong(item, "updatedAt");
            result.Add(new AiThreadSummary
            {
                Id = ReadString(item, "id") ?? string.Empty,
                Title = title,
                ConversationTitle = conversationTitle,
                LatestUserMessage = latestUserMessage,
                Status = status,
                ModelName = ReadString(item, "model"),
                ReasoningEffort = ReadString(item, "reasoningEffort"),
                RolloutPath = ReadString(item, "path"),
                UpdatedAt = updated > 0 ? DateTimeOffset.FromUnixTimeSeconds(updated) : null
            });
        }
        return result;
    }

    private async Task<AiThreadSummary> EnrichFromLatestTurnAsync(AiThreadSummary summary,
        CancellationToken cancellationToken)
    {
        var detail = await TryRequestAsync("thread/read", new JsonObject
        {
            ["threadId"] = summary.Id,
            ["includeTurns"] = true
        }, TimeSpan.FromSeconds(7), cancellationToken);
        if (detail is not { } root || !root.TryGetProperty("thread", out var thread)) return summary;

        var status = thread.TryGetProperty("status", out var threadStatus)
            ? ParseThreadStatus(threadStatus)
            : summary.Status;
        if (thread.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in turns.EnumerateArray().Reverse())
            {
                status = ReadString(turn, "status") ?? status;
                break;
            }
        }
        // A separately launched app-server cannot observe another Codex process's in-memory
        // active flag. The shared rollout is authoritative for the latest execution boundary
        // and is appended while the desktop task is running.
        var rolloutPath = ReadString(thread, "path") ?? summary.RolloutPath;
        status = ReadRolloutExecutionStatus(rolloutPath, status);
        var conversationTitle = ReadString(thread, "name") ?? ReadString(thread, "title") ??
                                summary.ConversationTitle;
        var latestUserMessage = FindLatestUserMessage(thread) ?? summary.LatestUserMessage;
        return new AiThreadSummary
        {
            Id = summary.Id,
            Title = SelectTaskTitle(conversationTitle, latestUserMessage, summary.Title),
            ConversationTitle = conversationTitle,
            LatestUserMessage = latestUserMessage,
            Status = status,
            ModelName = ReadString(thread, "model") ?? summary.ModelName,
            ReasoningEffort = ReadString(thread, "reasoningEffort") ?? summary.ReasoningEffort,
            RolloutPath = rolloutPath,
            UpdatedAt = ReadLong(thread, "updatedAt") is var updated && updated > 0
                ? DateTimeOffset.FromUnixTimeSeconds(updated)
                : summary.UpdatedAt
        };
    }

    private static string ParseThreadStatus(JsonElement statusElement)
    {
        if (statusElement.ValueKind == JsonValueKind.String)
            return statusElement.GetString() ?? "notLoaded";
        if (statusElement.ValueKind != JsonValueKind.Object) return "notLoaded";

        var status = ReadString(statusElement, "type") ?? "notLoaded";
        if (status != "active" || !statusElement.TryGetProperty("activeFlags", out var flags) ||
            flags.ValueKind != JsonValueKind.Array) return status;

        var activeFlags = flags.EnumerateArray().Select(flag => flag.GetString()).ToHashSet();
        if (activeFlags.Contains("waitingOnApproval")) return "waitingOnApproval";
        return activeFlags.Contains("waitingOnUserInput") ? "waitingOnUserInput" : status;
    }

    private static string NormalizeTitle(string title)
    {
        title = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return title.Length > 64 ? title[..64] + "…" : title;
    }

    private static string? FindLatestUserMessage(JsonElement thread)
    {
        if (!thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array) return null;
        foreach (var turn in turns.EnumerateArray().Reverse())
        {
            var direct = ReadString(turn, "userMessage") ?? ReadString(turn, "prompt");
            if (IsUsefulTitle(direct)) return CleanUserMessage(direct!);
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray().Reverse())
            {
                if (!IsUserMessageItem(item)) continue;
                var text = ReadMessageContent(item);
                if (IsUsefulTitle(text)) return CleanUserMessage(text!);
            }
        }
        return null;
    }

    private static bool IsUserMessageItem(JsonElement item)
    {
        var role = ReadString(item, "role");
        var type = ReadString(item, "type");
        return string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "userMessage", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "user_message", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadMessageContent(JsonElement item)
    {
        var direct = ReadString(item, "text") ?? ReadString(item, "message");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        if (!item.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        return string.Join(" ", content.EnumerateArray()
            .Select(part => ReadString(part, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text))!);
    }

    private string ReadRolloutExecutionStatus(string? path, string fallback)
    {
        if (path?.StartsWith(@"\\?\", StringComparison.Ordinal) == true) path = path[4..];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return fallback;
        var cursor = _rolloutCursors.GetOrAdd(path, _ => new RolloutCursor());
        try
        {
            lock (cursor.Sync)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                if (stream.Length < cursor.Offset)
                {
                    cursor.Offset = 0;
                    cursor.Status = null;
                    cursor.ThreadId = null;
                    cursor.FirstUserMessage = null;
                    cursor.LatestUserMessage = null;
                    cursor.ModelName = null;
                    cursor.ReasoningEffort = null;
                    cursor.ThreadSource = null;
                    cursor.PendingLine.SetLength(0);
                }
                stream.Position = cursor.Offset;
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var segmentStart = 0;
                    for (var index = 0; index < read; index++)
                    {
                        if (buffer[index] != (byte)'\n') continue;
                        cursor.PendingLine.Write(buffer, segmentStart, index - segmentStart);
                        ParseRolloutLine(cursor);
                        cursor.PendingLine.SetLength(0);
                        segmentStart = index + 1;
                    }
                    if (segmentStart < read)
                        cursor.PendingLine.Write(buffer, segmentStart, read - segmentStart);
                    cursor.Offset += read;
                }
                return cursor.Status ?? fallback;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[AiManager] Rollout status unavailable: {ex.Message}");
        }
        return cursor.Status ?? fallback;
    }

    private static void ParseRolloutLine(RolloutCursor cursor)
    {
        if (cursor.PendingLine.Length == 0) return;
        var line = Encoding.UTF8.GetString(cursor.PendingLine.GetBuffer(), 0, (int)cursor.PendingLine.Length);
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 256 });
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload)) return;
            switch (ReadString(root, "type"))
            {
                case "session_meta":
                    cursor.ThreadId = ReadString(payload, "id") ?? ReadString(payload, "session_id") ?? cursor.ThreadId;
                    cursor.ThreadSource = ReadString(payload, "thread_source") ?? cursor.ThreadSource;
                    break;
                case "turn_context":
                    cursor.ModelName = ReadString(payload, "model") ?? cursor.ModelName;
                    cursor.ReasoningEffort = ReadString(payload, "effort") ??
                                             ReadString(payload, "reasoning_effort") ??
                                             cursor.ReasoningEffort;
                    break;
                case "response_item":
                    CaptureLocalUserMessage(cursor, payload);
                    break;
                case "event_msg":
                    var eventType = ReadString(payload, "type");
                    if (string.Equals(eventType, "user_message", StringComparison.OrdinalIgnoreCase))
                        CaptureLocalUserText(cursor, ReadString(payload, "message"));
                    cursor.Status = eventType switch
                    {
                        "task_started" => "inProgress",
                        "task_complete" => "completed",
                        "turn_aborted" => string.Equals(ReadString(payload, "reason"), "interrupted",
                            StringComparison.OrdinalIgnoreCase) ? "interrupted" : "failed",
                        _ => cursor.Status
                    };
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore a malformed record and continue with the next complete JSONL line.
        }
    }

    private static void CaptureLocalUserMessage(RolloutCursor cursor, JsonElement payload)
    {
        if (!string.Equals(ReadString(payload, "type"), "message", StringComparison.Ordinal) ||
            !string.Equals(ReadString(payload, "role"), "user", StringComparison.Ordinal) ||
            !payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        var parts = content.EnumerateArray()
            .Where(item => string.Equals(ReadString(item, "type"), "input_text", StringComparison.Ordinal))
            .Select(item => ReadString(item, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text));
        CaptureLocalUserText(cursor, string.Join(" ", parts!).Trim());
    }

    private static void CaptureLocalUserText(RolloutCursor cursor, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var title = CleanUserMessage(text);
        if (string.IsNullOrWhiteSpace(title) ||
            title.StartsWith("<turn_aborted>", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("<recommended_plugins>", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("<app-context>", StringComparison.OrdinalIgnoreCase))
            return;

        cursor.FirstUserMessage ??= title;
        cursor.LatestUserMessage = title;
    }

    private static string CleanUserMessage(string text)
    {
        var title = text.Trim();
        const string requestMarker = "## My request:";
        var marker = title.LastIndexOf(requestMarker, StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) title = title[(marker + requestMarker.Length)..].Trim();
        return title;
    }

    private static (string? DisplayName, string? ReasoningEffort) ResolveModel(
        JsonElement? root, string? preferredModel)
    {
        if (root is not { } modelRoot || !modelRoot.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return (preferredModel, null);

        JsonElement? fallback = null;
        foreach (var item in data.EnumerateArray())
        {
            var id = ReadString(item, "model") ?? ReadString(item, "id");
            if (!string.IsNullOrWhiteSpace(preferredModel) &&
                string.Equals(id, preferredModel, StringComparison.OrdinalIgnoreCase))
                return (ReadString(item, "displayName") ?? id, ReadString(item, "defaultReasoningEffort"));
            if (item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True)
                fallback = item.Clone();
        }
        if (fallback is { } selected)
            return (ReadString(selected, "displayName") ?? ReadString(selected, "model"),
                ReadString(selected, "defaultReasoningEffort"));
        return (preferredModel, null);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && JsonProtocolValue.TryGetInt32(value, out var number) ? number : 0;
    private static long ReadLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && JsonProtocolValue.TryGetInt64(value, out var number) ? number : 0;
    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && JsonProtocolValue.TryGetDouble(value, out var number) ? number : 0;

    public async ValueTask DisposeAsync()
    {
        foreach (var cursor in _rolloutCursors.Values) cursor.PendingLine.Dispose();
        _rolloutCursors.Clear();
        foreach (var cursor in _localUsageCursors.Values) cursor.PendingLine.Dispose();
        _localUsageCursors.Clear();
        await _client.DisposeAsync();
    }
}
