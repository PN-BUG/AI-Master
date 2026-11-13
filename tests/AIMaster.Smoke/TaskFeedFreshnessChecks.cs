using System;
using System.Linq;
using System.Threading.Tasks;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyTaskFeedFreshness()
    {
        var stamp = DateTimeOffset.UtcNow;
        var first = TaskSummary("freshness-first", "First conversation", "inProgress", stamp);
        var second = TaskSummary("freshness-second", "Second conversation", "completed", stamp.AddMinutes(-1));
        AiTaskSnapshotFeed.Reset();
        AiTaskSnapshotFeed.Publish(AiProviderModes.Codex, new[] { first, second });
        var remoteStart = AiTaskSnapshotFeed.CurrentRevision;
        var waiting = AiTaskSnapshotFeed.Update(AiProviderModes.Codex, current => new[]
        {
            CopyTask(current[1], status: "waitingOnUserInput", latest: "A fresh question"), current[0]
        });
        var rejected = AiTaskSnapshotFeed.PublishIfCurrent(AiProviderModes.Codex, new[] { first, second }, remoteStart);
        Check(ReferenceEquals(rejected, waiting) &&
              rejected.Tasks.Single(task => task.Id == second.Id) is { Status: "waitingOnUserInput",
                  LatestUserMessage: "A fresh question" },
            "a remote batch started before local input cannot restore stale task status, order or naming");

        var waitingStart = waiting.Revision;
        var resumed = AiTaskSnapshotFeed.Update(AiProviderModes.Codex, current => current.Select(task => task.Id == second.Id
            ? CopyTask(task, status: "inProgress", title: "Latest selected request", latest: "A resumed request")
            : task).ToArray());
        rejected = AiTaskSnapshotFeed.PublishIfCurrent(AiProviderModes.Codex, waiting.Tasks, waitingStart);
        Check(ReferenceEquals(rejected, resumed) &&
              rejected.Tasks.Single(task => task.Id == second.Id) is { Status: "inProgress",
                  Title: "Latest selected request" },
            "a late remote batch cannot restore waiting input or replace a newer selected task name");

        var metadata = AiTaskSnapshotFeed.PublishIfCurrent(AiProviderModes.Codex, resumed.Tasks.Select(task => task.Id == second.Id
            ? CopyTask(task, title: "Server conversation name", model: "fresh-model", effort: "high")
            : task).ToArray(), resumed.Revision);
        var rebased = AiTaskSnapshotFeed.Update(AiProviderModes.Codex, current => current.Select(task => task.Id == second.Id
            ? CopyTask(task, status: "waitingOnUserInput") : task).ToArray());
        Check(metadata.Revision > resumed.Revision &&
              rebased.Tasks.Single(task => task.Id == second.Id) is { Title: "Server conversation name",
                  LatestUserMessage: "A resumed request", ModelName: "fresh-model", ReasoningEffort: "high" } rebasedSecond &&
              rebasedSecond.ConversationTitle == second.ConversationTitle,
            "an atomic local rebase preserves the latest server names, model, effort and request metadata");

        var notifications = 0;
        void CountChange(AiTaskSnapshot _) => notifications++;
        AiTaskSnapshotFeed.Changed += CountChange;
        try
        {
            var before = AiTaskSnapshotFeed.CurrentRevision;
            var unchanged = AiTaskSnapshotFeed.Update(AiProviderModes.Codex, current => current.Select(task => CopyTask(task)).ToArray());
            AiTaskSnapshotFeed.Publish(AiProviderModes.Codex, unchanged.Tasks.Select(task => CopyTask(task)).ToArray());
            var guardedUnchanged = AiTaskSnapshotFeed.PublishIfCurrent(AiProviderModes.Codex, unchanged.Tasks, before);
            Check(notifications == 0 && AiTaskSnapshotFeed.CurrentRevision == before &&
                  ReferenceEquals(unchanged, guardedUnchanged),
                "semantically unchanged local and regular task refreshes do not repaint or advance revisions");

            // The unified view has a canonical status/time order, so a reversed publisher list is
            // the same view and must not repaint; a real status change must.
            AiTaskSnapshotFeed.Update(AiProviderModes.Codex, current => current.Reverse().ToArray());
            Check(notifications == 0 && AiTaskSnapshotFeed.CurrentRevision == before,
                "the unified task view keeps a canonical order instead of repainting on list shuffles");

            AiTaskSnapshotFeed.Update(AiProviderModes.Codex, current => current.Select(task =>
                CopyTask(task, status: task.Status == "inProgress" ? "waitingOnApproval" : task.Status)).ToArray());
            Check(notifications == 1 && AiTaskSnapshotFeed.CurrentRevision == before + 1,
                "a task state change publishes a new revision");
        }
        finally
        {
            AiTaskSnapshotFeed.Changed -= CountChange;
        }

        var callbackOutsideLock = true;
        var reentered = false;
        void Reenter(AiTaskSnapshot _)
        {
            // Reading on another thread detects lock ownership; Monitor is reentrant on one thread.
            var read = Task.Run(() => AiTaskSnapshotFeed.CurrentRevision);
            callbackOutsideLock &= read.Wait(TimeSpan.FromSeconds(2));
            if (reentered) return;
            reentered = true;
            AiTaskSnapshotFeed.Update(AiProviderModes.Codex, current => current.Select(task => CopyTask(task, effort: "low")).ToArray());
        }
        AiTaskSnapshotFeed.Changed += Reenter;
        try
        {
            AiTaskSnapshotFeed.Update(AiProviderModes.Codex, current => current.Select(task => CopyTask(task, effort: "medium")).ToArray());
            Check(callbackOutsideLock && reentered &&
                  AiTaskSnapshotFeed.Latest!.Tasks.All(task => task.ReasoningEffort == "low"),
                "task feed subscribers run outside the publication lock and can publish a later update");
        }
        finally
        {
            AiTaskSnapshotFeed.Changed -= Reenter;
        }

        static AiThreadSummary TaskSummary(string id, string title, string status, DateTimeOffset updated) => new()
        {
            Id = id, Title = title, ConversationTitle = title, LatestUserMessage = "Original request",
            Status = status, ModelName = "original-model", ReasoningEffort = "medium",
            RolloutPath = id + ".jsonl", UpdatedAt = updated
        };

        static AiThreadSummary CopyTask(AiThreadSummary task, string? status = null, string? title = null,
            string? latest = null, string? model = null, string? effort = null) => new()
        {
            Id = task.Id, Title = title ?? task.Title, ConversationTitle = task.ConversationTitle,
            LatestUserMessage = latest ?? task.LatestUserMessage, Status = status ?? task.Status,
            ModelName = model ?? task.ModelName, ReasoningEffort = effort ?? task.ReasoningEffort,
            RolloutPath = task.RolloutPath, UpdatedAt = task.UpdatedAt
        };
    }
}
