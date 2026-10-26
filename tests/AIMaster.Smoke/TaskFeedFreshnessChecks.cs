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
        AiTaskSnapshotFeed.Publish(new[] { first, second });
        var remoteStart = AiTaskSnapshotFeed.CurrentRevision;
        var waiting = AiTaskSnapshotFeed.Update(current => new[]
        {
            CopyTask(current[1], status: "waitingOnUserInput", latest: "A fresh question"), current[0]
        });
        var rejected = AiTaskSnapshotFeed.PublishIfCurrent(new[] { first, second }, remoteStart);
        Check(ReferenceEquals(rejected, waiting) && rejected.Tasks[0].Id == second.Id &&
              rejected.Tasks[0].Status == "waitingOnUserInput" &&
              rejected.Tasks[0].LatestUserMessage == "A fresh question",
            "a remote batch started before local input cannot restore stale task status, order or naming");

        var waitingStart = waiting.Revision;
        var resumed = AiTaskSnapshotFeed.Update(current => current.Select(task => task.Id == second.Id
            ? CopyTask(task, status: "inProgress", title: "Latest selected request", latest: "A resumed request")
            : task).ToArray());
        rejected = AiTaskSnapshotFeed.PublishIfCurrent(waiting.Tasks, waitingStart);
        Check(ReferenceEquals(rejected, resumed) && rejected.Tasks[0].Status == "inProgress" &&
              rejected.Tasks[0].Title == "Latest selected request",
            "a late remote batch cannot restore waiting input or replace a newer selected task name");

        var metadata = AiTaskSnapshotFeed.PublishIfCurrent(resumed.Tasks.Select(task => task.Id == second.Id
            ? CopyTask(task, title: "Server conversation name", model: "fresh-model", effort: "high")
            : task).ToArray(), resumed.Revision);
        var rebased = AiTaskSnapshotFeed.Update(current => current.Select(task => task.Id == second.Id
            ? CopyTask(task, status: "waitingOnUserInput") : task).ToArray());
        Check(metadata.Revision > resumed.Revision && rebased.Tasks[0].Title == "Server conversation name" &&
              rebased.Tasks[0].ConversationTitle == second.ConversationTitle &&
              rebased.Tasks[0].LatestUserMessage == "A resumed request" &&
              rebased.Tasks[0].ModelName == "fresh-model" && rebased.Tasks[0].ReasoningEffort == "high",
            "an atomic local rebase preserves the latest server names, model, effort and request metadata");

        var notifications = 0;
        void CountChange(AiTaskSnapshot _) => notifications++;
        AiTaskSnapshotFeed.Changed += CountChange;
        try
        {
            var before = AiTaskSnapshotFeed.CurrentRevision;
            var unchanged = AiTaskSnapshotFeed.Update(current => current.Select(task => CopyTask(task)).ToArray());
            AiTaskSnapshotFeed.Publish(unchanged.Tasks.Select(task => CopyTask(task)).ToArray());
            var guardedUnchanged = AiTaskSnapshotFeed.PublishIfCurrent(unchanged.Tasks, before);
            Check(notifications == 0 && AiTaskSnapshotFeed.CurrentRevision == before &&
                  ReferenceEquals(unchanged, guardedUnchanged),
                "semantically unchanged local and regular task refreshes do not repaint or advance revisions");

            AiTaskSnapshotFeed.Update(current => current.Reverse().ToArray());
            Check(notifications == 1 && AiTaskSnapshotFeed.CurrentRevision == before + 1,
                "a task order change publishes a new revision");
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
            AiTaskSnapshotFeed.Update(current => current.Select(task => CopyTask(task, effort: "low")).ToArray());
        }
        AiTaskSnapshotFeed.Changed += Reenter;
        try
        {
            AiTaskSnapshotFeed.Update(current => current.Select(task => CopyTask(task, effort: "medium")).ToArray());
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
