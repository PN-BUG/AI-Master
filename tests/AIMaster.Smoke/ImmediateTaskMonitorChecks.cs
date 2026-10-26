using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyImmediateTaskMonitoring()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-immediate-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "live.jsonl");
        var id = Guid.NewGuid().ToString();
        File.WriteAllLines(path, new[]
        {
            Record("session_meta", new { id, thread_source = "user" }),
            Record("event_msg", new { type = "task_started" }),
            Record("turn_context", new { model = "test-model", turn_id = "live-turn" }),
            Record("event_msg", new { type = "user_message", message = "Live monitor fixture" })
        });
        var staleWriteTime = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(path, staleWriteTime);
        for (var index = 0; index < 70; index++)
            File.WriteAllLines(Path.Combine(sessions, $"agent-{index:00}.jsonl"), new[]
            {
                Record("session_meta", new { id = "agent-" + index, thread_source = "guardian" }),
                Record("event_msg", new { type = "task_started" })
            });
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var service = new AiManagerService(Path.Combine(root, "monitor.json"));
        var attention = new ResetSignalAttention(Path.Combine(root, "attention.json"));
        var main = new AiManagerWindow(new AiManagerService(Path.Combine(root, "main.json")), attention);
        var floating = new AiFloatingWindow(new AiManagerService(Path.Combine(root, "floating.json")), attention);
        AiTaskSnapshotFeed.Publish(Array.Empty<AiThreadSummary>());
        var monitor = new TaskFileChangeMonitor(sessions, token =>
        {
            service.RefreshLocalTaskSnapshot(sessions, token);
            return System.Threading.Tasks.Task.CompletedTask;
        });
        try
        {
            monitor.Start();
            PumpUntil(() => StatusIs("inProgress"), "initial local state reaches both windows");
            var timer = Stopwatch.StartNew();
            AppendRecords(new[]
            {
                Record("response_item", new { type = "function_call", name = "request_user_input_async",
                    status = "completed", call_id = "live-question", arguments = "{\"questions\":[{\"title\":\"Fixture\"}]}" }),
                Record("response_item", new { type = "function_call_output", call_id = "live-question", output = "{\"accepted\":true}" })
            });
            PumpUntil(() => StatusIs("waitingOnUserInput"), "file append immediately highlights both windows without a network refresh");
            Check(timer.ElapsedMilliseconds < 1500 && ((TextBlock)floating.FindName("TaskStatusText")).FontWeight == FontWeights.Bold,
                $"real file change reaches waiting highlight in {timer.ElapsedMilliseconds} ms");
            Check(File.GetLastWriteTimeUtc(path) == staleWriteTime &&
                  AiTaskSnapshotFeed.Latest!.Tasks.Single(task => task.Id == id).UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1),
                "an open writer and stale filesystem time cannot hide current user status behind 70 agent logs");
            var reply = "<send_user_message_question_reply>" + JsonSerializer.Serialize(new[]
            {
                new { questionItemId = "[\"request_user_input_async\",\"live-question\",0]", answer = "Fixture answer" }
            }) + "</send_user_message_question_reply>";
            timer.Restart();
            AppendRecords(new[] { Record("event_msg", new { type = "user_message", message = reply }) });
            PumpUntil(() => StatusIs("inProgress"), "reply file append immediately clears the cached waiting highlight");
            Check(timer.ElapsedMilliseconds < 1500,
                $"real reply change restores running in {timer.ElapsedMilliseconds} ms");
            var local = service.ReadLocalTask(path, File.GetLastWriteTimeUtc(path))!;
            var stale = new AiThreadSummary { Id = id, Title = "Live monitor fixture", Status = "waitingOnUserInput",
                ModelName = "test-model", RolloutPath = path };
            var refreshed = service.BuildTaskSummariesAsync(new[] { stale }, new[] { local }, appServerAvailable: false)
                .GetAwaiter().GetResult();
            Check(refreshed.Single().Status == "inProgress",
                "a stale remote waiting flag fetched after the reply cannot restore resolved local input");
            AppendRecords(new[] { Record("event_msg", new { type = "task_complete" }) });
            PumpUntil(() => StatusIs("completed"), "completion immediately clears waiting state in both windows");
            local = service.ReadLocalTask(path, File.GetLastWriteTimeUtc(path))!;
            refreshed = service.BuildTaskSummariesAsync(new[] { stale }, new[] { local }, appServerAvailable: false)
                .GetAwaiter().GetResult();
            Check(refreshed.Single().Status == "completed",
                "a stale remote waiting flag cannot resurrect input after local turn completion");
        }
        finally
        {
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            // The application owns and closes the two window services at shutdown.
        }

        bool StatusIs(string status) => ((ListBox)main.FindName("ThreadList")).Items.OfType<AiThreadSummary>()
            .Any(task => task.Id == id && task.Status == status) &&
            ((TextBlock)floating.FindName("TaskStatusText")).Text == LocalizationService.FormatTaskStatus(status);
        void AppendRecords(string[] records)
        {
            writer.Write(Encoding.UTF8.GetBytes(string.Join("\n", records) + "\n"));
            writer.Flush();
            File.SetLastWriteTimeUtc(path, staleWriteTime);
        }
        static string Record(string type, object payload) => JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, type, payload });
    }

    private static void PumpUntil(Func<bool> predicate, string message)
    {
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (predicate() || elapsed.Elapsed > TimeSpan.FromSeconds(3)) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Check(predicate(), message);
    }
}
