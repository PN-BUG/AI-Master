using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyTaskFeedWindows()
    {
        var isolated = Path.Combine(Path.GetTempPath(), "AIMaster-task-feed-" + Guid.NewGuid().ToString("N"));
        var attention = new ResetSignalAttention(Path.Combine(isolated, "attention.json"));
        var mainService = new AiManagerService(Path.Combine(isolated, "main.json"));
        var floatingService = new AiManagerService(Path.Combine(isolated, "floating.json"));
        var main = new AiManagerWindow(mainService, attention);
        var floating = new AiFloatingWindow(floatingService, attention);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var renderFloating = typeof(AiFloatingWindow).GetMethod("RenderSnapshot", flags)!;
        var renderMain = typeof(AiManagerWindow).GetMethod("RenderSnapshot", flags)!;
        var getFloatingSnapshot = typeof(AiFloatingWindow).GetField("_lastSnapshot", flags)!;
        renderFloating.Invoke(floating, new object[] { new AiFloatingSnapshot { RemainingPercent = 63 } });
        var tasks = Enumerable.Range(0, 16).Select(index => new AiThreadSummary
        {
            Id = index.ToString(), Title = "Task " + index,
            Status = index == 0 ? "waitingOnUserInput" : "inProgress", ModelName = "gpt-6"
        }).ToList();
        AiTaskSnapshotFeed.Publish(tasks);
        var first = AiTaskSnapshotFeed.Latest!;
        var dashboard = ((ListBox)main.FindName("ThreadList")).Items.Cast<AiThreadSummary>().ToList();
        var floatSnapshot = (AiFloatingSnapshot)getFloatingSnapshot.GetValue(floating)!;
        Check(dashboard.Count == 12 && floatSnapshot.Tasks.Count == 8 &&
              dashboard.Take(8).Select(Key).SequenceEqual(floatSnapshot.Tasks.Select(Key)),
            "one task publication updates dashboard and floating IDs, order, titles and states together");
        Check(floatSnapshot.TaskStatus == "waitingOnUserInput" && floatSnapshot.RemainingPercent == 63,
            "shared task updates immediately show waiting input without replacing quota data");
        renderFloating.Invoke(floating, new object[] { new AiFloatingSnapshot { Tasks = tasks,
            TaskName = tasks[0].Title, TaskStatus = tasks[0].Status, ModelName = "GPT-6",
            ReasoningEffort = "HIGH", RemainingPercent = 63 } });
        floatSnapshot = (AiFloatingSnapshot)getFloatingSnapshot.GetValue(floating)!;
        Check(floatSnapshot.ModelName == "GPT-6" && floatSnapshot.ReasoningEffort == "HIGH",
            "shared task projection preserves resolved model names and default reasoning effort");

        var resumed = tasks.Select(task => new AiThreadSummary
        {
            Id = task.Id, Title = task.Title + " updated", Status = "inProgress", ModelName = task.ModelName
        }).ToList();
        AiTaskSnapshotFeed.Publish(resumed);
        // A slower quota refresh may finish after a more recent task-only refresh.
        renderMain.Invoke(main, new object[] { new AiManagerSnapshot { Threads = tasks } });
        renderFloating.Invoke(floating, new object[] { new AiFloatingSnapshot { Tasks = tasks,
            TaskName = "stale task", TaskStatus = "waitingOnUserInput", RemainingPercent = 62 } });
        typeof(AiManagerWindow).GetMethod("TaskSnapshot_Changed", flags)!.Invoke(main, new object[] { first });
        typeof(AiFloatingWindow).GetMethod("TaskSnapshot_Changed", flags)!.Invoke(floating, new object[] { first });
        dashboard = ((ListBox)main.FindName("ThreadList")).Items.Cast<AiThreadSummary>().ToList();
        floatSnapshot = (AiFloatingSnapshot)getFloatingSnapshot.GetValue(floating)!;
        Check(dashboard[0].Title == "Task 0 updated" && floatSnapshot.TaskName == "Task 0 updated" &&
              floatSnapshot.TaskStatus == "inProgress" && floatSnapshot.RemainingPercent == 62 &&
              dashboard.Take(8).Select(Key).SequenceEqual(floatSnapshot.Tasks.Select(Key)),
            "late quota results and old callbacks cannot restore stale waiting tasks in either window");

        // App shutdown closes these windows and disposes their owned services.
        static string Key(AiThreadSummary task) => $"{task.Id}|{task.Title}|{task.Status}";
    }
}
