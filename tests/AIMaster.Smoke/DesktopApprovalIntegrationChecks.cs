using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyDesktopApprovalIntegrationChecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster-desktop-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "rollout.jsonl");
        var runtime = new AiManagerRuntime();
        var service = new AiManagerService(Path.Combine(directory, "settings.json"), runtime: runtime);
        try
        {
            File.WriteAllText(path,
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread\",\"thread_source\":\"user\"}}\n" +
                "{\"type\":\"turn_context\",\"payload\":{\"model\":\"fixture-model\"}}\n" +
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n");
            AiTaskSnapshotFeed.Publish(Array.Empty<AiThreadSummary>());
            var initial = Refresh();
            Check(initial.Status == "inProgress", "local task starts running without desktop approval evidence");
            Check(runtime.DesktopTasks.GetTrackedThreadIds().SequenceEqual(["thread"]),
                "task candidates register the exact conversation ids for passive desktop observation");

            var oldRevision = AiTaskSnapshotFeed.CurrentRevision;
            runtime.ObserveDesktopState(ApprovalSnapshot(1, [ApprovalRequest("approve")]));
            var approval = Refresh();
            Check(approval.Status == "waitingOnApproval" &&
                  AiTaskSnapshotFeed.ApplyToFloating(new AiFloatingSnapshot(), [approval]).TaskStatus == "waitingOnApproval",
                "an actual desktop pending request reaches the common task feed and floating status");
            var guarded = AiTaskSnapshotFeed.PublishIfCurrent([initial], oldRevision);
            Check(guarded.Tasks.Single().Status == "waitingOnApproval", "a delayed regular refresh cannot overwrite the approval feed");

            // No command output or rollout append: answering the request alone resumes its status.
            runtime.ObserveDesktopState(ApprovalPatch(1, 2,
                new { op = "replace", path = new object[] { "requests" }, value = Array.Empty<object>() }));
            Check(Refresh().Status == "inProgress", "answering the real approval clears its badge before command output");

            runtime.ObserveDesktopState(ApprovalSnapshot(3, [ApprovalRequest("second")]));
            Check(Refresh().Status == "waitingOnApproval", "a later approval is independently displayed");
            runtime.DesktopTasks.Clear();
            Check(Refresh().Status == "inProgress", "disconnect restores the rollout status instead of retaining the previous approval overlay");

            runtime.ObserveDesktopState(ApprovalSnapshot(4, [ApprovalRequest("third")]));
            Refresh();
            runtime.ObserveDesktopState(ApprovalPatch(7, 8));
            Check(Refresh().Status == "inProgress", "a missing desktop patch falls back to local execution until a fresh snapshot");

            runtime.ObserveDesktopState(ApprovalSnapshot(9, [], new { type = "idle" }));
            Check(Refresh().Status == "completed", "the owning desktop's idle state overrides an older running rollout");
            runtime.ObserveDesktopState(ApprovalSnapshot(10, []));
            File.AppendAllText(path, "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"request_user_input_async\",\"call_id\":\"question\",\"arguments\":\"{\\\"questions\\\":[{}]}\"}}\n");
            Check(Refresh().Status == "waitingOnUserInput", "a running desktop flag cannot hide an actual pending asynchronous question");
            runtime.ObserveDesktopState(ApprovalSnapshot(11, [ApprovalRequest("with-question")]));
            Check(Refresh().Status == "waitingOnApproval", "real approval takes priority over an independently pending question");
            runtime.ObserveDesktopState(ApprovalPatch(11, 12,
                new { op = "replace", path = new object[] { "requests" }, value = Array.Empty<object>() }));
            Check(Refresh().Status == "waitingOnUserInput", "answering approval restores the independent unanswered question");
            runtime.DesktopTasks.Clear();
            File.AppendAllText(path, "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}\n");
            Check(Refresh().Status == "completed", "rollout completion remains available after desktop observation ends");

            var candidates = Enumerable.Range(0, 32).Select(index => new AiThreadSummary
            {
                Id = index == 0 ? "thread" : "capacity-" + index,
                Status = "inProgress", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(index)
            }).ToArray();
            service.BuildTaskSummariesAsync([], candidates, appServerAvailable: false).GetAwaiter().GetResult();
            runtime.ObserveDesktopState(ApprovalSnapshot(1, [ApprovalRequest("oldest")]));
            var withNewerTask = candidates.Append(new AiThreadSummary
                { Id = "newest", Status = "inProgress", UpdatedAt = DateTimeOffset.UtcNow.AddDays(1) });
            var bounded = service.BuildTaskSummariesAsync([], withNewerTask, appServerAvailable: false).GetAwaiter().GetResult();
            Check(bounded.Count == 32 && bounded[0].Id == "thread" && bounded[0].Status == "waitingOnApproval",
                "a newer thirty-third task cannot evict an already observed pending approval before sorting");
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            AiTaskSnapshotFeed.Publish(Array.Empty<AiThreadSummary>());
            Directory.Delete(directory, recursive: true);
        }

        AiThreadSummary Refresh()
        {
            var local = service.ReadLocalTask(path, DateTime.UtcNow)!;
            var tasks = service.BuildTaskSummariesAsync(AiTaskSnapshotFeed.Latest?.Tasks ?? [], [local],
                appServerAvailable: false).GetAwaiter().GetResult();
            AiTaskSnapshotFeed.Publish(tasks);
            return tasks.Single();
        }
    }
}
