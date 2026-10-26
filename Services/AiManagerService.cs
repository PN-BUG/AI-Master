using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIMaster.Models;

namespace AIMaster.Services;

internal sealed class AiManagerService : IAsyncDisposable
{
    private const int TaskCandidateLimit = 32;
    private const int LocalTaskScanLimit = 64;
    private static readonly SemaphoreSlim TaskRefreshGate = new(1, 1);
    private readonly AiManagerRuntime _runtime;
    private readonly bool _ownsRuntime;
    private readonly CodexAppServerClient _client;
    private readonly SharedUsageClient _sharedUsageClient;
    private readonly SemaphoreSlim _sharedUsageGate = new(1, 1);
    private SharedUsageCounter? _sharedUsageCounter;
    private string? _sharedUsageCounterDevice;
    private readonly Dictionary<string, SharedFileStamp> _sharedUsageFileVersions = new(StringComparer.OrdinalIgnoreCase);
    private sealed record SharedFileStamp(long Length, DateTime LastWriteUtc);
    private sealed record SharedUsageScan(List<SharedUsageSample> Samples,
        Dictionary<string, SharedFileStamp> Versions, HashSet<string> Seen);
    public bool CanRestoreSharedUsage
    {
        get
        {
            try { return GetSharedUsageCounter(Settings.SharedUsageDeviceId).CanRestoreMode(Settings.SharedUsageResetMode); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
    }
    private readonly string _settingsPath;
    private readonly string? _sharedUsageSourceHome;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AiLimitWindow>>>? _sharedUsageLimitsReader;
    private readonly ConcurrentDictionary<string, RolloutCursor> _rolloutCursors;
    private readonly ConcurrentDictionary<string, LocalUsageCursor> _localUsageCursors;
    private readonly Dictionary<string, string> _monitoredTaskStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly RolloutActivityIndex _rolloutActivityIndex;
    private List<AiLimitWindow> _floatingLimits = new();
    private DateTimeOffset _floatingLimitsAt;
    private DateTimeOffset _floatingLimitsAttemptAt;
    private DateTimeOffset _nextConnectAttemptAt;
    private JsonElement? _floatingModelsRoot;
    private DateTimeOffset _floatingModelsAt;
    private DateTimeOffset _floatingModelsAttemptAt;

    internal abstract class CachedCursor
    {
        public readonly object Sync = new();
        public int ActiveReaders;
        public long LastAccessTimestamp;
        public readonly RolloutRecordProjector PendingLine = new();
    }

    internal sealed class RolloutCursor : CachedCursor
    {
        public readonly RolloutInputTracker InputTracker = new();
        public long Offset;
        public string? Status;
        public string? ThreadId;
        public string? FirstUserMessage;
        public string? LatestUserMessage;
        public string? ModelName;
        public string? ReasoningEffort;
        public string? ThreadSource;
        public DateTimeOffset? LatestRecordAt;
    }

    internal sealed class LocalUsageCursor : CachedCursor
    {
        public readonly List<LocalUsageRecord> Records = new();
        public long Offset;
        public string? ProjectPath;
        public string? ModelName;
        public bool HasTokenUsageRecords;
    }

    internal sealed record LocalUsageRecord(
        DateOnly Date,
        int Hour,
        string ProjectPath,
        string? ModelName,
        long Tokens,
        bool IsLegacy = false,
        DateTimeOffset? Timestamp = null);

    internal sealed record LocalSummaryEntry(long Length, DateTime LastWriteUtc,
        DateOnly Start, DateOnly End, LocalUsageRecord[] Records);

    public AiManagerService(string? settingsPath = null, string? sharedUsageSourceHome = null,
        SharedUsageClient? sharedUsageClient = null,
        Func<CancellationToken, Task<IReadOnlyList<AiLimitWindow>>>? sharedUsageLimitsReader = null,
        AiManagerRuntime? runtime = null)
    {
        _runtime = runtime ?? new AiManagerRuntime();
        _ownsRuntime = runtime is null;
        _client = _runtime.Client;
        _rolloutCursors = _runtime.RolloutCursors;
        _localUsageCursors = _runtime.LocalUsageCursors;
        _rolloutActivityIndex = _runtime.ActivityIndex;
        _sharedUsageClient = sharedUsageClient ?? new SharedUsageClient();
        _settingsPath = settingsPath ?? AppPaths.GetSettingsFile();
        _sharedUsageSourceHome = sharedUsageSourceHome;
        _sharedUsageLimitsReader = sharedUsageLimitsReader;
        Settings = LoadSettings();
    }

    public AiManagerSettings Settings { get; private set; }
    public AiManagerSnapshot? LastSnapshot { get; private set; }
    public string? LastQuotaError { get; private set; }
    internal string AccountResetStatePath => Path.Combine(Path.GetDirectoryName(_settingsPath)!, "account-reset-state.json");

    public async Task<AiManagerSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try { await _client.ConnectAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastQuotaError = ex.Message;
            // A disconnected remote process must not stop persisted local tasks from updating.
            await RefreshTaskSummariesAsync(appServerAvailable: false, cancellationToken);
            throw;
        }
        // These requests are independent. Starting them together removes a full optional-request
        // round trip from first paint while the local JSONL work stays off the UI thread.
        var rateTask = _client.RequestAsync("account/rateLimits/read",
            cancellationToken: cancellationToken, timeout: TimeSpan.FromSeconds(45));
        var usageTask = TryRequestAsync("account/usage/read", null, TimeSpan.FromSeconds(12), cancellationToken);
        var tasksTask = RefreshTaskSummariesAsync(appServerAvailable: true, cancellationToken);
        var localUsageTask = Task.Run(() => DiscoverLocalUsage(cancellationToken), cancellationToken);

        // Publish task state independently of the slower quota requests, including when a
        // quota request later fails. Both windows receive this same canonical task list.
        var tasks = await tasksTask;
        JsonElement rate;
        try { rate = await rateTask; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastQuotaError = ex.Message;
            throw;
        }
        var usage = await usageTask;
        var localUsage = await localUsageTask;
        var limits = ParseLimits(rate);
        AiSharedUsageSummary sharedUsage;
        try { sharedUsage = await SyncSharedUsageAsync(cancellationToken, limits: limits); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            sharedUsage = new AiSharedUsageSummary { Enabled = Settings.SharedUsageEnabled,
                Error = LocalizationService.L("本机共享计数读取或保存失败：", "Unable to read or save local shared counts: ") + ex.Message };
        }

        LastQuotaError = null;
        LastSnapshot = new AiManagerSnapshot
        {
            Limits = limits,
            DailyUsage = usage is { } usageValue ? ParseDailyUsage(usageValue) : new(),
            LifetimeTokens = usage is { } usageSummary ? ParseLifetimeTokens(usageSummary) : null,
            Threads = tasks.Take(12).ToList(),
            ResetCredits = ParseResetCredits(rate),
            LocalUsage = localUsage,
            SharedUsage = sharedUsage,
            CapturedAt = DateTimeOffset.Now
        };
        return LastSnapshot;
    }

    public async Task<AiFloatingSnapshot> RefreshFloatingAsync(CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var appServerAvailable = await TryConnectFloatingAsync(cancellationToken, forceRefresh);
        var now = DateTimeOffset.Now;
        var refreshLimits = forceRefresh || now - _floatingLimitsAttemptAt >= TimeSpan.FromMinutes(1);
        var refreshModels = forceRefresh || now - _floatingModelsAttemptAt >=
            TimeSpan.FromMinutes(_floatingModelsRoot is null ? 1 : 10);
        if (appServerAvailable && refreshLimits) _floatingLimitsAttemptAt = now;
        if (appServerAvailable && refreshModels) _floatingModelsAttemptAt = now;

        var tasksTask = RefreshTaskSummariesAsync(appServerAvailable, cancellationToken);
        var rateTask = appServerAvailable && refreshLimits
            ? TryReadFloatingQuotaAsync(cancellationToken)
            : Task.FromResult<JsonElement?>(null);
        var modelsTask = appServerAvailable && refreshModels
            ? TryRequestAsync("model/list", new JsonObject
            {
                ["limit"] = 100,
                ["includeHidden"] = true
            }, TimeSpan.FromSeconds(8), cancellationToken)
            : Task.FromResult<JsonElement?>(null);
        var tasks = await tasksTask;
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

        var visibleTasks = tasks.Take(8).ToList();
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
            QuotaError = LastQuotaError,
            QuotaCapturedAt = _floatingLimitsAt == default ? null : _floatingLimitsAt,
            CapturedAt = DateTimeOffset.Now
        };
    }

    private async Task<List<AiThreadSummary>> RefreshTaskSummariesAsync(bool appServerAvailable,
        CancellationToken cancellationToken)
    {
        // Main and floating windows own separate clients. Serialize the complete read and
        // publish cycle so an older request cannot overwrite a newer task snapshot.
        await TaskRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var taskRevision = AiTaskSnapshotFeed.CurrentRevision;
            var localTasksTask = Task.Run(() =>
                DiscoverLocalTasks(LocalTaskScanLimit, TaskCandidateLimit, cancellationToken), cancellationToken);
            var threadsTask = appServerAvailable
                ? TryRequestAsync("thread/list", BuildThreadListParameters(TaskCandidateLimit, interactiveOnly: true),
                    TimeSpan.FromSeconds(10), cancellationToken)
                : Task.FromResult<JsonElement?>(null);
            var threadsRoot = await threadsTask;
            var localTasks = await localTasksTask;
            var serverTasks = threadsRoot is { } root ? ParseThreads(root) : new List<AiThreadSummary>();
            var tasks = await BuildTaskSummariesAsync(serverTasks, localTasks, appServerAvailable, cancellationToken);
            return AiTaskSnapshotFeed.PublishIfCurrent(tasks, taskRevision).Tasks.ToList();
        }
        finally
        {
            TaskRefreshGate.Release();
        }
    }

    internal async Task<List<AiThreadSummary>> BuildTaskSummariesAsync(IEnumerable<AiThreadSummary> serverTasks,
        IEnumerable<AiThreadSummary> localTasks, bool appServerAvailable = true,
        CancellationToken cancellationToken = default)
    {
        ReloadTaskNameSource();
        // A separately launched app-server may omit sessions owned by the desktop. Merge
        // persisted local sessions before enrichment and apply limits only after final status sorting.
        var candidates = MergeFloatingTasks(serverTasks, localTasks).Where(IsUsefulFloatingTask).ToList();
        var enriched = await Task.WhenAll(candidates.Select(item =>
            appServerAvailable || (!string.IsNullOrWhiteSpace(item.RolloutPath) && !string.IsNullOrWhiteSpace(item.ModelName))
                ? EnrichFloatingTaskAsync(item, cancellationToken)
                : Task.FromResult(item)));
        var ordered = SortTasksByStatusAndTime(ApplyDesktopStatuses(enriched.Where(IsUsefulFloatingTask)))
            .Take(TaskCandidateLimit)
            .ToList();
        _runtime.DesktopTasks.SetTrackedThreads(ordered.Select(task => task.Id));
        return ordered;
    }

    internal AiTaskSnapshot RefreshLocalTaskSnapshot(string sessionsRoot, CancellationToken cancellationToken = default)
    {
        var scanStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        ReloadTaskNameSource();
        var localTasks = DiscoverLocalTasks(sessionsRoot, LocalTaskScanLimit, TaskCandidateLimit, cancellationToken);
        var changedStates = localTasks.Where(task =>
                (_monitoredTaskStates.TryGetValue(task.Id, out var previous) && previous != task.Status) ||
                task.Status == "waitingOnUserInput")
            .Select(task => task.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = AiTaskSnapshotFeed.Update(current =>
        {
            var candidates = SortTasksByStatusAndTime(ApplyDesktopStatuses(MergeFloatingTasksCore(current, localTasks, changedStates)))
                .Take(TaskCandidateLimit).ToList();
            _runtime.DesktopTasks.SetTrackedThreads(candidates.Select(task => task.Id));
            return candidates;
        });
        _monitoredTaskStates.Clear();
        foreach (var task in localTasks) _monitoredTaskStates[task.Id] = task.Status;
        TaskMonitorTrace.Record("scan", revision: snapshot.Revision,
            detail: $"selected={localTasks.Count}; elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds:0}");
        return snapshot;
    }

    private void ReloadTaskNameSource()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            Settings.TaskNameSource = TaskNameSources.Normalize(ReadString(document.RootElement, "taskNameSource"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[AiManager] Task name preference unavailable: {ex.Message}");
        }
    }

    private async Task<JsonElement?> TryReadFloatingQuotaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.RequestAsync("account/rateLimits/read", cancellationToken: cancellationToken,
                timeout: TimeSpan.FromSeconds(8));
            LastQuotaError = null;
            return result;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
        {
            LastQuotaError = ex.Message;
            return null;
        }
    }

    private async Task<bool> TryConnectFloatingAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (_client.IsConnected) return true;
        if (!forceRefresh && DateTimeOffset.Now < _nextConnectAttemptAt) return false;
        _nextConnectAttemptAt = DateTimeOffset.Now.AddSeconds(30);
        try
        {
            await _client.ConnectAsync(cancellationToken);
            _nextConnectAttemptAt = default;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastQuotaError = ex.Message;
            System.Diagnostics.Debug.WriteLine("[AiManager] App-server unavailable; using local sessions.");
            return false;
        }
    }

    private static bool IsUsefulFloatingTask(AiThreadSummary thread) =>
        !string.IsNullOrWhiteSpace(thread.Id) &&
        !thread.Title.StartsWith("The following is the Codex agent history", StringComparison.OrdinalIgnoreCase);

    internal static IOrderedEnumerable<AiThreadSummary> SortTasksByStatusAndTime(IEnumerable<AiThreadSummary> tasks) =>
        tasks.OrderBy(item => TaskStatusOrder(item.Status))
            .ThenByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase);

    private static int TaskStatusOrder(string status) => status switch
    {
        "waitingOnApproval" => 0,
        "waitingOnUserInput" => 1,
        "active" or "inProgress" => 2,
        "failed" or "systemError" => 3,
        "interrupted" => 4,
        "idle" or "completed" => 5,
        "notLoaded" => 6,
        _ => 7
    };

    internal static string ResolveExecutionStatus(string threadStatus, string executionStatus) =>
        // Waiting flags describe live interaction. Turn/rollout boundaries only describe
        // execution and can still refer to the previous turn while approval/input is pending.
        threadStatus is "waitingOnApproval" or "waitingOnUserInput" ? threadStatus : executionStatus;

    private List<AiThreadSummary> DiscoverLocalTasks(int scanLimit, int resultLimit,
        CancellationToken cancellationToken = default)
    {
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configuredHome;
        return DiscoverLocalTasks(Path.Combine(codexHome, "sessions"), scanLimit, resultLimit, cancellationToken);
    }

    internal List<AiThreadSummary> DiscoverLocalTasks(string sessionsRoot, int scanLimit, int resultLimit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(sessionsRoot)) return new();
            _runtime.PruneCaches();

            var candidates = _rolloutActivityIndex.GetCandidates(sessionsRoot, cancellationToken)
                .Where(item => item.IsUserSession)
                .Take(scanLimit)
                .ToList();
            var tasks = new Dictionary<string, AiThreadSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var task = ReadLocalTask(item.Path, item.ActivityAtUtc);
                if (task is null || !IsUsefulFloatingTask(task) || tasks.ContainsKey(task.Id)) continue;
                tasks[task.Id] = task;
            }
            return SortTasksByStatusAndTime(ApplyDesktopStatuses(tasks.Values))
                .Take(resultLimit)
                .Select(ApplyTaskNamePreference)
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
        var status = ReadRolloutExecutionStatus(path, "completed", out var cursor);
        if (cursor is null) return null;
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
                UpdatedAt = cursor.LatestRecordAt ?? new DateTimeOffset(DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc))
            };
        }
    }

    private IEnumerable<AiThreadSummary> MergeFloatingTasks(
        IEnumerable<AiThreadSummary> serverTasks,
        IEnumerable<AiThreadSummary> localTasks) => MergeFloatingTasksCore(serverTasks, localTasks);

    private IEnumerable<AiThreadSummary> MergeFloatingTasksCore(
        IEnumerable<AiThreadSummary> serverTasks,
        IEnumerable<AiThreadSummary> localTasks,
        IReadOnlySet<string>? changedLocalStates = null)
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
                Status = changedLocalStates?.Contains(local.Id) == true && server.Status != "waitingOnApproval"
                    ? local.Status
                    : ResolveExecutionStatus(server.Status, local.Status),
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
        Status = item.StatusBeforeDesktopObservation ?? item.Status,
        ModelName = item.ModelName,
        ReasoningEffort = item.ReasoningEffort,
        RolloutPath = item.RolloutPath,
        UpdatedAt = item.UpdatedAt
    };

    private IEnumerable<AiThreadSummary> ApplyDesktopStatuses(IEnumerable<AiThreadSummary> tasks)
    {
        foreach (var task in tasks)
        {
            var status = _runtime.DesktopTasks.GetStatus(task.Id);
            // Async desktop questions can remain unanswered while execution continues.
            // The rollout tracker has the exact reply identity; a coarse running flag cannot dismiss it.
            if (status == "inProgress" && task.Status == "waitingOnUserInput") status = null;
            if (status is null) { yield return task; continue; }
            yield return new AiThreadSummary
            {
                Id = task.Id, Title = task.Title, ConversationTitle = task.ConversationTitle,
                LatestUserMessage = task.LatestUserMessage, Status = status,
                StatusBeforeDesktopObservation = task.StatusBeforeDesktopObservation ?? task.Status,
                ModelName = task.ModelName, ReasoningEffort = task.ReasoningEffort,
                RolloutPath = task.RolloutPath, UpdatedAt = task.UpdatedAt
            };
        }
    }

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

    private static string? FirstUsefulTitle(params string?[] values) =>
        values.Select(TaskTitleFormatter.Clean).FirstOrDefault(IsUsefulTitle);

    private static bool IsUsefulTitle(string? value) =>
        !string.IsNullOrWhiteSpace(TaskTitleFormatter.Clean(value)) &&
        value != "未命名任务" &&
        value?.StartsWith("The following is the Codex agent history", StringComparison.OrdinalIgnoreCase) != true;

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
        settings.SharedUsageSyncKeyProtected = LocalSecretProtection.Protect(settings.SharedUsageSyncKey);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
        Settings = settings;
    }

    public AiManagerSettings ReloadSettings()
    {
        Settings = LoadSettings();
        return Settings;
    }

    public async Task<AiSharedUsageSummary> SyncSharedUsageAsync(
        CancellationToken cancellationToken = default,
        SharedUsageCounterAction action = SharedUsageCounterAction.None,
        IReadOnlyList<AiLimitWindow>? limits = null)
    {
        await _sharedUsageGate.WaitAsync(cancellationToken);
        try
        {
            // Snapshot settings so a UI edit cannot change device identity midway through a sync.
            var settings = JsonSerializer.Deserialize<AiManagerSettings>(JsonSerializer.Serialize(Settings))!;
            settings.SharedUsageSyncKey = Settings.SharedUsageSyncKey;
            if (!settings.SharedUsageEnabled && action == SharedUsageCounterAction.None)
                return new AiSharedUsageSummary { Enabled = false };
            var counter = GetSharedUsageCounter(settings.SharedUsageDeviceId);
            if (limits is null)
            {
                if (_sharedUsageLimitsReader is not null) limits = await _sharedUsageLimitsReader(cancellationToken);
                else
                {
                    limits = LastQuotaError is null ? LastSnapshot?.Limits ?? _floatingLimits : [];
                    if (settings.SharedUsageResetMode == SharedUsageResetModes.AccountReset && _client.IsConnected)
                    {
                        var rate = await TryRequestAsync("account/rateLimits/read", null, TimeSpan.FromSeconds(8), cancellationToken);
                        if (rate is { } currentRate) limits = ParseLimits(currentRate);
                    }
                }
            }
            var usage = await Task.Run(() =>
            {
                // Capture available history for this policy's manual baseline, keeping the
                // other policies' independent baselines untouched.
                var scan = ReadSharedUsageSamples(action == SharedUsageCounterAction.Clear || counter.RequiresTimestampMigration
                    ? SharedUsageResetModes.Never : settings.SharedUsageResetMode, counter.RequiresTimestampMigration, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var summary = counter.Update(scan.Samples, settings.SharedUsageResetMode, DateTimeOffset.Now, limits, action);
                // A failed save must retry the same observations next time. Commit fingerprints
                // only after the ledger has accepted them, never merely after reading a file.
                foreach (var (path, version) in scan.Versions) _sharedUsageFileVersions[path] = version;
                foreach (var path in _sharedUsageFileVersions.Keys.Where(path => !scan.Seen.Contains(path)).ToArray())
                    _sharedUsageFileVersions.Remove(path);
                return summary;
            }, cancellationToken);
            // An unknown boundary must not upload a fake zero or lifetime total as reset-period usage.
            if (usage.Error is not null)
                return new AiSharedUsageSummary { Enabled = settings.SharedUsageEnabled, Error = usage.Error, CountedFrom = usage.CountedFrom };
            var response = await _sharedUsageClient.SyncAsync(settings, usage, cancellationToken);
            return new AiSharedUsageSummary
            {
                Enabled = response.Enabled, Error = response.Error, SyncedAt = response.SyncedAt,
                Devices = response.Devices, TotalTokens = response.TotalTokens, CountedFrom = usage.CountedFrom
            };
        }
        finally { _sharedUsageGate.Release(); }
    }

    private SharedUsageCounter GetSharedUsageCounter(string deviceId)
    {
        if (_sharedUsageCounter is null || _sharedUsageCounterDevice != deviceId)
        {
            _sharedUsageCounter = new SharedUsageCounter(Path.Combine(Path.GetDirectoryName(_settingsPath)!,
                $"shared-usage-{deviceId}.json"), Settings.SharedUsageResetMode);
            _sharedUsageCounterDevice = deviceId;
            _sharedUsageFileVersions.Clear();
        }
        return _sharedUsageCounter;
    }

    private SharedUsageScan ReadSharedUsageSamples(string mode, bool forceRead, CancellationToken cancellationToken)
    {
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = _sharedUsageSourceHome ?? (string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configuredHome);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var earliestWriteUtc = monday.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var samples = new List<SharedUsageSample>();
        var versions = new Dictionary<string, SharedFileStamp>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Archived rollouts keep the same session identity and must not count a second time.
        foreach (var folder in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(codexHome, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                seen.Add(path);
                var file = new FileInfo(path);
                if (mode == SharedUsageResetModes.Weekly && file.LastWriteTimeUtc < earliestWriteUtc) continue;
                var version = new SharedFileStamp(file.Length, file.LastWriteTimeUtc);
                if (!forceRead && _sharedUsageFileVersions.TryGetValue(path, out var previous) && previous == version) continue;
                var cursor = ScanLocalUsageFile(path, DateOnly.FromDateTime(file.LastWriteTime), cancellationToken);
                var sessionId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(path))));
                lock (cursor.Sync)
                    samples.AddRange(cursor.Records.GroupBy(item => (item.Date, item.ModelName, item.Timestamp))
                        .Select(group => new SharedUsageSample(sessionId, group.Key.Date, group.Key.ModelName,
                            group.Sum(item => item.Tokens), group.Key.Timestamp)));
                versions[path] = version;
            }
        }
        return new SharedUsageScan(samples, versions, seen);
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

    public AiForecast BuildLocalForecast(AiManagerSnapshot snapshot)
    {
        var localUsage = snapshot.LocalUsage;
        var dailyTokens = localUsage.DailyUsage.Sum(item => (double)item.Tokens) / 7d;
        var main = snapshot.MainLimit;
        if (main?.ResetsAt is null || main.WindowDurationMinutes <= 0 || main.UsedPercent <= 0)
            return new AiForecast(dailyTokens, 0, null, false,
                LocalizationService.L("积累更多额度数据后可预测本机消耗", "More quota data is needed before forecasting local usage"));

        var accountTokens = snapshot.DailyUsage
            .Where(item => item.Date >= localUsage.PeriodStart && item.Date <= localUsage.PeriodEnd)
            .Sum(item => item.Tokens);
        if (accountTokens <= 0 || localUsage.TotalTokens <= 0 || localUsage.TotalTokens > accountTokens)
            return new AiForecast(dailyTokens, 0, null, false,
                localUsage.TotalTokens <= 0
                    ? LocalizationService.L("本周暂无本机消耗，暂不生成预测", "No local usage this week; no forecast is available")
                    : LocalizationService.L("账户 Token 历史不足，暂无法估算本机预测", "Account token history is incomplete; local forecast is unavailable"));

        var start = main.ResetsAt.Value.AddMinutes(-main.WindowDurationMinutes);
        var elapsedDays = Math.Max((snapshot.CapturedAt - start).TotalDays, 1d / 24d);
        var localShare = localUsage.TotalTokens / (double)accountTokens;
        var dailyPercent = main.UsedPercent * localShare / elapsedDays;
        if (dailyPercent <= 0.001)
            return new AiForecast(dailyTokens, dailyPercent, null, false,
                LocalizationService.L("本机当前消耗很低，预计不会单独耗尽账号额度", "Local usage is low and is unlikely to exhaust the account quota on its own"));

        var exhaustion = snapshot.CapturedAt.AddDays(main.RemainingPercent / dailyPercent);
        var beforeReset = exhaustion < main.ResetsAt.Value;
        var projectedPercent = Math.Min(999,
            main.UsedPercent + dailyPercent * (main.ResetsAt.Value - snapshot.CapturedAt).TotalDays);
        var summary = beforeReset
            ? LocalizationService.IsEnglish
                ? $"At this device's current rate, the account quota may be exhausted by {exhaustion.LocalDateTime.ToString("MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture)}"
                : $"按本机当前速度，账号额度预计 {exhaustion.LocalDateTime:M月d日 HH:mm} 触顶"
            : LocalizationService.IsEnglish
                ? $"At this device's current rate, account usage may reach about {projectedPercent:0}% by the end of this window"
                : $"按本机当前速度，本周期结束时账号约使用 {projectedPercent:0}%";
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
        var weekEnd = weekStart.AddDays(6);
        if (!Directory.Exists(sessionsRoot))
        {
            PruneUsageSummaries(sessionsRoot, null);
            return new AiLocalUsageSummary { PeriodStart = weekStart, PeriodEnd = weekEnd };
        }
        var today = DateOnly.FromDateTime(DateTime.Now);
        var rollingStart = today.AddDays(-6);
        var scanStart = rollingStart < weekStart ? rollingStart : weekStart;
        var earliestWriteUtc = scanStart.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var sessionRecords = new List<(string SessionPath, LocalUsageRecord Record)>();
        var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            var updatedAt = file.LastWriteTimeUtc;
            if (updatedAt < earliestWriteUtc) continue;
            eligible.Add(path);
            if (!_runtime.UsageSummaries.TryGetValue(path, out var cached) || cached.Length != file.Length ||
                cached.LastWriteUtc != updatedAt || cached.Start != scanStart || cached.End != weekEnd)
            {
                var cursor = ScanLocalUsageFile(path, DateOnly.FromDateTime(updatedAt.ToLocalTime()), cancellationToken);
                lock (cursor.Sync)
                {
                    // Dashboard statistics need hour/project/model totals, not a retained
                    // object for every historical token event. Keep the compact recent view
                    // independently of the bounded detailed parser cache.
                    var records = cursor.Records.Where(item => item.Date >= scanStart && item.Date <= weekEnd)
                        .GroupBy(item => (item.Date, item.Hour, item.ProjectPath, item.ModelName))
                        .Select(group => new LocalUsageRecord(group.Key.Date, group.Key.Hour,
                            group.Key.ProjectPath, group.Key.ModelName, group.Sum(item => item.Tokens)))
                        .ToArray();
                    cached = new LocalSummaryEntry(file.Length, updatedAt, scanStart, weekEnd, records);
                }
                lock (_runtime.CacheSync)
                    if (!_runtime.IsDisposed) _runtime.UsageSummaries[path] = cached;
            }
            sessionRecords.AddRange(cached.Records.Select(item => (path, item)));
        }
        PruneUsageSummaries(sessionsRoot, eligible);

        var weeklyRecords = sessionRecords
            .Where(item => item.Record.Date >= weekStart && item.Record.Date <= weekEnd)
            .ToList();
        var total = weeklyRecords.Sum(item => item.Record.Tokens);
        var todayRecords = weeklyRecords.Where(item => item.Record.Date == today).ToList();
        var projects = weeklyRecords
            .GroupBy(item => item.Record.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AiProjectUsage
            {
                ProjectName = ProjectDisplayName(group.Key),
                ProjectPath = group.Key,
                Tokens = group.Sum(item => item.Record.Tokens),
                SharePercent = total > 0 ? group.Sum(item => item.Record.Tokens) * 100d / total : 0,
                SessionCount = group.Select(item => item.SessionPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                MostUsedModel = SelectMostUsedModel(group.Select(item => item.Record))
            })
            .OrderByDescending(item => item.Tokens)
            .ThenBy(item => item.ProjectName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new AiLocalUsageSummary
        {
            PeriodStart = weekStart,
            PeriodEnd = weekEnd,
            TotalTokens = total,
            TodayTokens = todayRecords.Sum(item => item.Record.Tokens),
            SessionCount = weeklyRecords.Select(item => item.SessionPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            MostUsedModel = SelectMostUsedModel(weeklyRecords.Select(item => item.Record)),
            DailyUsage = Enumerable.Range(0, 7)
                .Select(offset => rollingStart.AddDays(offset))
                .Select(date => new AiDailyUsage
                {
                    Date = date,
                    Tokens = sessionRecords.Where(item => item.Record.Date == date)
                        .Sum(item => item.Record.Tokens)
                })
                .ToList(),
            TodayHourlyUsage = Enumerable.Range(0, 24)
                .Select(hour => new AiHourlyUsage
                {
                    Hour = hour,
                    Tokens = todayRecords.Where(item => item.Record.Hour == hour)
                        .Sum(item => item.Record.Tokens)
                })
                .ToList(),
            Projects = projects
        };
    }

    private void PruneUsageSummaries(string sessionsRoot, HashSet<string>? eligible)
    {
        var prefix = Path.GetFullPath(sessionsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var path in _runtime.UsageSummaries.Keys)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (eligible is null || !eligible.Contains(path)))
                _runtime.UsageSummaries.TryRemove(path, out _);
    }

    private LocalUsageCursor ScanLocalUsageFile(string path, DateOnly fallbackDate, CancellationToken cancellationToken)
    {
        using var lease = _runtime.AcquireUsage(path);
        var cursor = lease.Value;
        lock (cursor.Sync)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length < cursor.Offset)
            {
                cursor.Offset = 0;
                cursor.ProjectPath = null;
                cursor.ModelName = null;
                cursor.HasTokenUsageRecords = false;
                cursor.Records.Clear();
                ResetPendingLine(cursor.PendingLine);
            }
            if (stream.Length == cursor.Offset) return cursor;
            stream.Position = cursor.Offset;
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var segmentStart = 0;
                    for (var index = 0; index < read; index++)
                    {
                        if (buffer[index] != (byte)'\n') continue;
                        cursor.PendingLine.Append(buffer.AsSpan(segmentStart, index - segmentStart));
                        ParseLocalUsageLine(cursor, fallbackDate);
                        ResetPendingLine(cursor.PendingLine);
                        segmentStart = index + 1;
                    }
                    if (segmentStart < read)
                        cursor.PendingLine.Append(buffer.AsSpan(segmentStart, read - segmentStart));
                    cursor.Offset += read;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        return cursor;
    }

    private static void ParseLocalUsageLine(LocalUsageCursor cursor, DateOnly fallbackDate)
    {
        var line = cursor.PendingLine.CompleteRecord();
        if (line.IsEmpty) return;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload)) return;
            var type = ReadString(root, "type");
            if (type is "session_meta" or "turn_context")
            {
                cursor.ProjectPath = NormalizeProjectPath(ReadString(payload, "cwd")) ?? cursor.ProjectPath;
                cursor.ModelName = NormalizeModelName(ReadString(payload, "model")) ?? cursor.ModelName;
                return;
            }
            var isLegacy = false;
            var tokens = type switch
            {
                // Current rollout records report one response at a time.
                "token_usage_record" when payload.TryGetProperty("usage", out var usage) &&
                                          usage.ValueKind == JsonValueKind.Object =>
                    ReadLong(usage, "total_tokens"),
                // Older token_count events expose both a cumulative total and the latest
                // response. Use the latter so repeated snapshots are never double-counted.
                "event_msg" when string.Equals(ReadString(payload, "type"), "token_count",
                                     StringComparison.OrdinalIgnoreCase) &&
                                 payload.TryGetProperty("info", out var info) &&
                                 info.ValueKind == JsonValueKind.Object &&
                                 info.TryGetProperty("last_token_usage", out var lastUsage) &&
                                 lastUsage.ValueKind == JsonValueKind.Object =>
                    ReadLegacyTokens(lastUsage, ref isLegacy),
                _ => 0
            };
            if (tokens <= 0) return;
            if (type == "token_usage_record" && !cursor.HasTokenUsageRecords)
            {
                cursor.HasTokenUsageRecords = true;
                cursor.Records.RemoveAll(item => item.IsLegacy);
            }
            if (isLegacy && cursor.HasTokenUsageRecords) return;
            var hasTimestamp = DateTimeOffset.TryParse(ReadString(root, "timestamp"), out var timestamp);
            var localDateTime = hasTimestamp
                ? timestamp.LocalDateTime
                : fallbackDate.ToDateTime(TimeOnly.MinValue);
            cursor.Records.Add(new LocalUsageRecord(DateOnly.FromDateTime(localDateTime), localDateTime.Hour,
                cursor.ProjectPath ?? LocalizationService.L("未归属项目", "Unassigned"),
                cursor.ModelName, tokens, isLegacy, hasTimestamp ? timestamp : null));
        }
        catch (JsonException)
        {
            // An incomplete or malformed record does not invalidate the rest of the session.
        }
    }

    private static long ReadLegacyTokens(JsonElement lastUsage, ref bool isLegacy)
    {
        isLegacy = true;
        return ReadLong(lastUsage, "total_tokens");
    }

    private static string? NormalizeProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(path.Trim());
    }

    private static string? NormalizeModelName(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    private static string? SelectMostUsedModel(IEnumerable<LocalUsageRecord> records) => records
        .Where(item => !string.IsNullOrWhiteSpace(item.ModelName))
        .GroupBy(item => item.ModelName!, StringComparer.OrdinalIgnoreCase)
        .Select(group => new { Name = group.Key, Tokens = group.Sum(item => item.Tokens) })
        .OrderByDescending(item => item.Tokens)
        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .Select(item => item.Name)
        .FirstOrDefault();

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
            settings.Theme = ThemeModes.Normalize(settings.Theme);
            settings.TaskNameSource = TaskNameSources.Normalize(settings.TaskNameSource);
            settings.FloatingFontScale = FloatingFontScales.Normalize(settings.FloatingFontScale);
            if (settings.FloatingWindowPlacement is { IsValid: false })
                settings.FloatingWindowPlacement = null;
            settings.DashboardCardLayout ??= new List<string>();
            settings.CollapsedDashboardCards ??= new List<string>();
            settings.DashboardCardSizes = settings.DashboardCardSizes is null
                ? new Dictionary<string, AiDashboardCardSize>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, AiDashboardCardSize>(settings.DashboardCardSizes,
                    StringComparer.OrdinalIgnoreCase);
            settings.SharedUsageServerUrl = string.IsNullOrWhiteSpace(settings.SharedUsageServerUrl)
                ? "https://www.woliux.com"
                : settings.SharedUsageServerUrl.Trim();
            settings.SharedUsageResetMode = SharedUsageResetModes.Normalize(settings.SharedUsageResetMode);
            if (Uri.TryCreate(settings.SharedUsageServerUrl, UriKind.Absolute, out var sharedUsageUri) &&
                (sharedUsageUri.Host.Equals("www.woliu.top", StringComparison.OrdinalIgnoreCase) ||
                 sharedUsageUri.Host.Equals("woliu.top", StringComparison.OrdinalIgnoreCase)) &&
                sharedUsageUri.AbsolutePath == "/" && string.IsNullOrEmpty(sharedUsageUri.Query) &&
                string.IsNullOrEmpty(sharedUsageUri.Fragment) && string.IsNullOrEmpty(sharedUsageUri.UserInfo) &&
                sharedUsageUri.IsDefaultPort)
                settings.SharedUsageServerUrl = "https://www.woliux.com";
            settings.SharedUsageSyncKey = LocalSecretProtection.Unprotect(settings.SharedUsageSyncKeyProtected);
            settings.SharedUsageDeviceId = Guid.TryParse(settings.SharedUsageDeviceId, out var deviceId)
                ? deviceId.ToString("D")
                : Guid.NewGuid().ToString("D");
            settings.SharedUsageDeviceName = string.IsNullOrWhiteSpace(settings.SharedUsageDeviceName)
                ? Environment.MachineName
                : settings.SharedUsageDeviceName.Trim();
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
            ? new[] { "cli", "vscode", "exec", "appServer", "unknown" }
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
            if (IsSubAgentThread(item)) continue;
            var conversationTitle = BoundTaskMetadata(ReadString(item, "name") ?? ReadString(item, "title"));
            var latestUserMessage = BoundTaskMetadata(ReadString(item, "preview"), preserveLastRequest: true);
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

    private static bool IsSubAgentThread(JsonElement thread)
    {
        if (!thread.TryGetProperty("source", out var source)) return false;
        return source.ValueKind switch
        {
            JsonValueKind.String => source.GetString()?.StartsWith("subAgent", StringComparison.OrdinalIgnoreCase) == true,
            JsonValueKind.Object => source.TryGetProperty("subAgent", out _),
            _ => false
        };
    }

    private async Task<AiThreadSummary> EnrichFromLatestTurnAsync(AiThreadSummary summary,
        CancellationToken cancellationToken)
    {
        var detail = await TryRequestAsync("thread/read", new JsonObject
        {
            ["threadId"] = summary.Id,
            ["includeTurns"] = false
        }, TimeSpan.FromSeconds(7), cancellationToken);
        if (detail is not { } root || !root.TryGetProperty("thread", out var thread)) return summary;

        var status = thread.TryGetProperty("status", out var threadStatus)
            ? ParseThreadStatus(threadStatus)
            : summary.Status;
        if (thread.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in turns.EnumerateArray().Reverse())
            {
                status = ResolveExecutionStatus(status, ReadString(turn, "status") ?? status);
                break;
            }
        }
        // A separately launched app-server cannot observe another Codex process's in-memory
        // active flag. The shared rollout is authoritative for the latest execution boundary
        // and is appended while the desktop task is running.
        var rolloutPath = ReadString(thread, "path") ?? summary.RolloutPath;
        status = ReadRolloutExecutionStatus(rolloutPath, status, out var localCursor);
        var conversationTitle = ReadString(thread, "name") ?? ReadString(thread, "title") ??
                                summary.ConversationTitle;
        var latestUserMessage = FindLatestUserMessage(thread) ?? summary.LatestUserMessage;
        var modelName = ReadString(thread, "model") ?? summary.ModelName;
        var reasoningEffort = ReadString(thread, "reasoningEffort") ?? summary.ReasoningEffort;
        if (localCursor is not null)
            lock (localCursor.Sync)
            {
                conversationTitle ??= localCursor.FirstUserMessage;
                latestUserMessage = localCursor.LatestUserMessage ?? latestUserMessage;
                modelName ??= localCursor.ModelName;
                reasoningEffort ??= localCursor.ReasoningEffort;
            }
        conversationTitle = BoundTaskMetadata(conversationTitle);
        latestUserMessage = BoundTaskMetadata(latestUserMessage, preserveLastRequest: true);
        return new AiThreadSummary
        {
            Id = summary.Id,
            Title = SelectTaskTitle(conversationTitle, latestUserMessage, summary.Title),
            ConversationTitle = conversationTitle,
            LatestUserMessage = latestUserMessage,
            Status = status,
            ModelName = modelName,
            ReasoningEffort = reasoningEffort,
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
        return BoundTaskMetadata(TaskTitleFormatter.Format(BoundTaskMetadata(title, preserveLastRequest: true)))!;
    }

    private static string? BoundTaskMetadata(string? text, bool preserveLastRequest = false)
    {
        const int maximumCharacters = 32 * 1024;
        const string ellipsis = " … ";
        if (text is null || text.Length <= maximumCharacters) return text;

        // Thread previews can include transport context before the actual request.
        // Select its last request before taking a bounded beginning/end window.
        const string marker = "## My request:";
        var request = preserveLastRequest ? text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase) : -1;
        var value = text.AsSpan(request >= 0 ? request : 0);
        if (value.Length <= maximumCharacters) return value.ToString();

        var headLength = (maximumCharacters - ellipsis.Length) / 2;
        var tailStart = value.Length - (maximumCharacters - ellipsis.Length - headLength);
        // A cut may discard a whole supplementary character, never half of it.
        if (char.IsHighSurrogate(value[headLength - 1]) && char.IsLowSurrogate(value[headLength])) headLength--;
        if (char.IsLowSurrogate(value[tailStart]) && char.IsHighSurrogate(value[tailStart - 1])) tailStart++;
        return string.Concat(value[..headLength], ellipsis.AsSpan(), value[tailStart..]);
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

    private string ReadRolloutExecutionStatus(string? path, string fallback) =>
        ReadRolloutExecutionStatus(path, fallback, out _);

    private string ReadRolloutExecutionStatus(string? path, string fallback, out RolloutCursor? result)
    {
        result = null;
        if (path?.StartsWith(@"\\?\", StringComparison.Ordinal) == true) path = path[4..];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return fallback;
        using var lease = _runtime.AcquireRollout(path);
        var cursor = result = lease.Value;
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
                    cursor.LatestRecordAt = null;
                    cursor.InputTracker.Reset();
                    ResetPendingLine(cursor.PendingLine);
                }
                if (stream.Length == cursor.Offset) return ResolveRolloutExecutionStatus(cursor, fallback);
                var statusBeforeRead = ResolveRolloutExecutionStatus(cursor, fallback);
                stream.Position = cursor.Offset;
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        var segmentStart = 0;
                        for (var index = 0; index < read; index++)
                        {
                            if (buffer[index] != (byte)'\n') continue;
                            cursor.PendingLine.Append(buffer.AsSpan(segmentStart, index - segmentStart));
                            ParseRolloutLine(cursor);
                            ResetPendingLine(cursor.PendingLine);
                            segmentStart = index + 1;
                        }
                        if (segmentStart < read)
                            cursor.PendingLine.Append(buffer.AsSpan(segmentStart, read - segmentStart));
                        cursor.Offset += read;
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                var status = ResolveRolloutExecutionStatus(cursor, fallback);
                if (cursor.Status is not null && status != statusBeforeRead)
                    TaskMonitorTrace.Record("parsed", cursor.ThreadId, status,
                        detail: $"offset={cursor.Offset}; recordAt={cursor.LatestRecordAt:O}");
                return status;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[AiManager] Rollout status unavailable: {ex.Message}");
        }
        return ResolveRolloutExecutionStatus(cursor, fallback);
    }

    private static string ResolveRolloutExecutionStatus(RolloutCursor cursor, string fallback)
    {
        var localStatus = cursor.InputTracker.IsWaiting ? "waitingOnUserInput" : cursor.Status ?? fallback;
        if (fallback == "waitingOnUserInput" && cursor.InputTracker.HasObservedRequest && !cursor.InputTracker.IsWaiting)
            return localStatus;
        return ResolveExecutionStatus(fallback, localStatus);
    }

    private static void ParseRolloutLine(RolloutCursor cursor)
    {
        var line = cursor.PendingLine.CompleteRecord();
        if (line.IsEmpty) return;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 256 });
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload)) return;
            if (root.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.String &&
                timestamp.TryGetDateTimeOffset(out var recordAt) &&
                (cursor.LatestRecordAt is null || recordAt > cursor.LatestRecordAt))
                cursor.LatestRecordAt = recordAt;
            cursor.InputTracker.Observe(ReadString(root, "type"), payload);
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

    private static void ResetPendingLine(RolloutRecordProjector pending) => pending.Reset();

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
        return TaskTitleFormatter.Clean(text);
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
        // A local clear may still be scanning on a worker when the window closes.
        await _sharedUsageGate.WaitAsync();
        try
        {
            _sharedUsageClient.Dispose();
            if (_ownsRuntime) await _runtime.DisposeAsync();
        }
        finally { _sharedUsageGate.Release(); }
    }
}
