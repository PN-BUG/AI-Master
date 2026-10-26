using AIMaster.Models;

namespace AIMaster.Services;

internal sealed record AiTaskSnapshot(long Revision, IReadOnlyList<AiThreadSummary> Tasks);

/// <summary>Publishes one ordered task view to both monitoring windows.</summary>
internal static class AiTaskSnapshotFeed
{
    private static readonly object Sync = new();
    private static AiTaskSnapshot? _latest;
    public static event Action<AiTaskSnapshot>? Changed;

    public static AiTaskSnapshot? Latest
    {
        get { lock (Sync) return _latest; }
    }

    public static long CurrentRevision
    {
        get { lock (Sync) return _latest?.Revision ?? 0; }
    }

    public static void Publish(IReadOnlyList<AiThreadSummary> tasks)
    {
        AiTaskSnapshot snapshot;
        bool changed;
        lock (Sync)
        {
            snapshot = PublishLocked(tasks, out changed);
        }
        if (changed) NotifyChanged(snapshot);
    }

    /// <summary>Commits a refresh only if no newer task state has arrived since it began.</summary>
    public static AiTaskSnapshot PublishIfCurrent(IReadOnlyList<AiThreadSummary> tasks, long expectedRevision)
    {
        AiTaskSnapshot snapshot;
        bool changed;
        lock (Sync)
        {
            if ((_latest?.Revision ?? 0) != expectedRevision)
                return _latest ?? new AiTaskSnapshot(0, Array.Empty<AiThreadSummary>());
            snapshot = PublishLocked(tasks, out changed);
        }
        if (changed) NotifyChanged(snapshot);
        return snapshot;
    }

    /// <summary>Rebases a local update on the latest metadata and publishes it atomically.</summary>
    public static AiTaskSnapshot Update(
        Func<IReadOnlyList<AiThreadSummary>, IReadOnlyList<AiThreadSummary>> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        AiTaskSnapshot snapshot;
        bool changed;
        lock (Sync)
        {
            snapshot = PublishLocked(update(_latest?.Tasks ?? Array.Empty<AiThreadSummary>()), out changed);
        }
        if (changed) NotifyChanged(snapshot);
        return snapshot;
    }

    private static void NotifyChanged(AiTaskSnapshot snapshot)
    {
        foreach (var task in snapshot.Tasks) TaskMonitorTrace.Record("feed", task.Id, task.Status, snapshot.Revision);
        Changed?.Invoke(snapshot);
    }

    private static AiTaskSnapshot PublishLocked(IReadOnlyList<AiThreadSummary> tasks, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var captured = tasks.ToArray();
        if (_latest is not null && _latest.Tasks.SequenceEqual(captured, TaskSummaryComparer.Instance))
        {
            changed = false;
            return _latest;
        }
        changed = true;
        return _latest = new AiTaskSnapshot((_latest?.Revision ?? 0) + 1, captured);
    }

    private sealed class TaskSummaryComparer : IEqualityComparer<AiThreadSummary>
    {
        public static readonly TaskSummaryComparer Instance = new();

        public bool Equals(AiThreadSummary? left, AiThreadSummary? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null &&
            left.Id == right.Id && left.Title == right.Title &&
            left.ConversationTitle == right.ConversationTitle &&
            left.LatestUserMessage == right.LatestUserMessage && left.Status == right.Status &&
            left.StatusBeforeDesktopObservation == right.StatusBeforeDesktopObservation &&
            left.ModelName == right.ModelName && left.ReasoningEffort == right.ReasoningEffort &&
            left.RolloutPath == right.RolloutPath && left.UpdatedAt == right.UpdatedAt;

        public int GetHashCode(AiThreadSummary item) => StringComparer.Ordinal.GetHashCode(item.Id);
    }

    internal static AiFloatingSnapshot ApplyToFloating(AiFloatingSnapshot previous, IReadOnlyList<AiThreadSummary> tasks)
    {
        var visible = tasks.Take(8).ToList();
        var focus = visible.FirstOrDefault();
        var sameModel = focus?.ModelName is { } model && previous.Tasks.FirstOrDefault()?.ModelName == model;
        return new AiFloatingSnapshot
        {
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
