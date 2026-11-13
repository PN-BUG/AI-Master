using AIMaster.Models;

namespace AIMaster.Services;

/// <summary>
/// One ordered task view. The provider completes the identity of the view; the task list itself is
/// unified across data sources and every task keeps its own <see cref="AiThreadSummary.Source"/>.
/// </summary>
internal sealed record AiTaskSnapshot(long Revision, IReadOnlyList<AiThreadSummary> Tasks, string Provider)
{
    public bool IsProvider(string? provider) =>
        string.Equals(AiProviderModes.Normalize(Provider), AiProviderModes.Normalize(provider),
            StringComparison.Ordinal);
}

/// <summary>
/// Publishes one unified, ordered task view of every data source to both monitoring windows.
/// Each publisher writes only its own source's slot; the feed merges the slots so the windows never
/// alternate between two providers' lists and can label every task with its source.
/// </summary>
internal static class AiTaskSnapshotFeed
{
    private const int UnifiedTaskLimit = 60;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, IReadOnlyList<AiThreadSummary>> SourceTasks = new(StringComparer.Ordinal);
    private static AiTaskSnapshot? _latest;
    private static string? _activeProvider;
    // Monotonic across resets so a test that clears the feed cannot make subscribers ignore the
    // next publication because it carries a lower revision than the one they already rendered.
    private static long _revision;

    public static event Action<AiTaskSnapshot>? Changed;

    /// <summary>Raised when the user switches data sources so every window reloads and refreshes.</summary>
    public static event Action<string>? ProviderChanged;

    public static AiTaskSnapshot? Latest
    {
        get { lock (Sync) return _latest; }
    }

    /// <summary>The unified task list of every data source, newest revision.</summary>
    public static IReadOnlyList<AiThreadSummary> Tasks
    {
        get { lock (Sync) return _latest?.Tasks ?? Array.Empty<AiThreadSummary>(); }
    }

    public static string ActiveProvider
    {
        get { lock (Sync) return _activeProvider ?? AiProviderModes.Codex; }
    }

    public static long CurrentRevision
    {
        get { lock (Sync) return _latest?.Revision ?? 0; }
    }

    /// <summary>Declares which data source the dashboard is showing; tasks of both stay visible.</summary>
    public static void ActivateProvider(string? provider)
    {
        var normalized = AiProviderModes.Normalize(provider);
        lock (Sync)
        {
            if (_activeProvider is not null &&
                string.Equals(_activeProvider, normalized, StringComparison.Ordinal)) return;
            _activeProvider = normalized;
        }
        ProviderChanged?.Invoke(normalized);
    }

    /// <summary>Clears all feed state; used by tests that exercise independent scenarios.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            SourceTasks.Clear();
            _latest = null;
            _activeProvider = null;
        }
    }

    public static void Publish(string? provider, IReadOnlyList<AiThreadSummary> tasks)
    {
        AiTaskSnapshot? snapshot;
        bool changed;
        lock (Sync) snapshot = PublishLocked(provider, tasks, out changed);
        if (changed && snapshot is not null) NotifyChanged(snapshot);
    }

    /// <summary>Commits a refresh only if no newer task state has arrived since it began.</summary>
    public static AiTaskSnapshot PublishIfCurrent(string? provider, IReadOnlyList<AiThreadSummary> tasks,
        long expectedRevision)
    {
        AiTaskSnapshot? published;
        AiTaskSnapshot snapshot;
        bool changed;
        lock (Sync)
        {
            if ((_latest?.Revision ?? 0) != expectedRevision)
                return _latest ?? EmptySnapshot(provider);
            published = PublishLocked(provider, tasks, out changed);
            snapshot = published ?? _latest ?? EmptySnapshot(provider);
        }
        if (changed) NotifyChanged(snapshot);
        return snapshot;
    }

    /// <summary>Rebases a local update on this source's latest tasks and publishes it atomically.</summary>
    public static AiTaskSnapshot Update(string? provider,
        Func<IReadOnlyList<AiThreadSummary>, IReadOnlyList<AiThreadSummary>> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        AiTaskSnapshot? published;
        AiTaskSnapshot snapshot;
        bool changed;
        lock (Sync)
        {
            var normalized = AiProviderModes.Normalize(provider);
            var current = SourceTasks.TryGetValue(normalized, out var tasks)
                ? tasks
                : Array.Empty<AiThreadSummary>();
            published = PublishLocked(normalized, update(current), out changed);
            snapshot = published ?? _latest ?? EmptySnapshot(normalized);
        }
        if (changed) NotifyChanged(snapshot);
        return snapshot;
    }

    private static void NotifyChanged(AiTaskSnapshot snapshot)
    {
        foreach (var task in snapshot.Tasks) TaskMonitorTrace.Record("feed", task.Id, task.Status, snapshot.Revision);
        Changed?.Invoke(snapshot);
    }

    private static AiTaskSnapshot? PublishLocked(string? provider, IReadOnlyList<AiThreadSummary> tasks,
        out bool changed)
    {
        changed = false;
        ArgumentNullException.ThrowIfNull(tasks);
        var normalized = AiProviderModes.Normalize(provider);
        // Stamp the publisher's source on every task and store the source slot in the canonical
        // order, so publishing the same view again (in any given order) is recognized as unchanged.
        var captured = AiManagerService.SortTasksByStatusAndTime(tasks
                .Select(task => task.IsDeepSeekSource == AiProviderModes.IsDeepSeek(normalized)
                    ? task
                    : CopyWithSource(task, normalized)))
            .ToArray();
        if (SourceTasks.TryGetValue(normalized, out var previous) &&
            previous.SequenceEqual(captured, TaskSummaryComparer.Instance))
            return _latest;
        SourceTasks[normalized] = captured;
        changed = true;
        return _latest = new AiTaskSnapshot(++_revision, MergeLocked(), normalized);
    }

    private static IReadOnlyList<AiThreadSummary> MergeLocked() =>
        AiManagerService.SortTasksByStatusAndTime(SourceTasks.Values.SelectMany(tasks => tasks))
            .Take(UnifiedTaskLimit)
            .ToList();

    private static AiThreadSummary CopyWithSource(AiThreadSummary task, string source) => new()
    {
        Id = task.Id,
        Title = task.Title,
        ConversationTitle = task.ConversationTitle,
        LatestUserMessage = task.LatestUserMessage,
        Status = task.Status,
        StatusBeforeDesktopObservation = task.StatusBeforeDesktopObservation,
        Source = source,
        ModelName = task.ModelName,
        ReasoningEffort = task.ReasoningEffort,
        RolloutPath = task.RolloutPath,
        ProjectPath = task.ProjectPath,
        UpdatedAt = task.UpdatedAt
    };

    private static AiTaskSnapshot EmptySnapshot(string? provider) =>
        new(_latest?.Revision ?? 0, Array.Empty<AiThreadSummary>(), AiProviderModes.Normalize(provider));

    private sealed class TaskSummaryComparer : IEqualityComparer<AiThreadSummary>
    {
        public static readonly TaskSummaryComparer Instance = new();

        public bool Equals(AiThreadSummary? left, AiThreadSummary? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null &&
            left.Id == right.Id && left.Title == right.Title &&
            left.ConversationTitle == right.ConversationTitle &&
            left.LatestUserMessage == right.LatestUserMessage && left.Status == right.Status &&
            left.StatusBeforeDesktopObservation == right.StatusBeforeDesktopObservation &&
            left.Source == right.Source &&
            left.ModelName == right.ModelName && left.ReasoningEffort == right.ReasoningEffort &&
            left.RolloutPath == right.RolloutPath && left.ProjectPath == right.ProjectPath &&
            left.UpdatedAt == right.UpdatedAt;

        public int GetHashCode(AiThreadSummary item) => StringComparer.Ordinal.GetHashCode(item.Id);
    }

    internal static AiFloatingSnapshot ApplyToFloating(AiFloatingSnapshot previous, IReadOnlyList<AiThreadSummary> tasks)
    {
        var visible = tasks.Take(8).ToList();
        var focus = visible.FirstOrDefault();
        var sameModel = focus?.ModelName is { } model && previous.Tasks.FirstOrDefault()?.ModelName == model;
        return new AiFloatingSnapshot
        {
            Provider = previous.Provider,
            FiveHourQuota = previous.FiveHourQuota,
            WeeklyQuota = previous.WeeklyQuota,
            QuotaText = previous.QuotaText,
            QuotaLabel = previous.QuotaLabel,
            QuotaDetail = previous.QuotaDetail,
            CodexFiveHourQuota = previous.CodexFiveHourQuota,
            CodexWeeklyQuota = previous.CodexWeeklyQuota,
            CodexQuotaError = previous.CodexQuotaError,
            CodexQuotaCapturedAt = previous.CodexQuotaCapturedAt,
            DeepSeekBalanceText = previous.DeepSeekBalanceText,
            DeepSeekBalanceDetail = previous.DeepSeekBalanceDetail,
            DeepSeekBalanceError = previous.DeepSeekBalanceError,
            DeepSeekBalanceCapturedAt = previous.DeepSeekBalanceCapturedAt,
            Tasks = visible,
            TaskName = focus?.DisplayTitle ?? LocalizationService.L("暂无最近任务", "No recent tasks"),
            TaskStatus = focus?.Status ?? "notLoaded",
            TaskStatusText = focus?.StatusText ?? LocalizationService.L("等待任务", "Waiting for a task"),
            ModelName = sameModel ? previous.ModelName : focus?.ModelName ?? LocalizationService.L("模型未知", "Unknown model"),
            ReasoningEffort = focus?.ReasoningEffort ?? (sameModel ? previous.ReasoningEffort : null),
            RemainingPercent = previous.RemainingPercent,
            QuotaWindow = previous.QuotaWindow,
            ResetsAt = previous.ResetsAt,
            QuotaError = previous.QuotaError,
            QuotaCapturedAt = previous.QuotaCapturedAt,
            CapturedAt = previous.CapturedAt
        };
    }
}
