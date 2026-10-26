using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyTaskConsistency()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aimaster-task-consistency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AiManagerSettings
        {
            TaskNameSource = TaskNameSources.ConversationTitle,
            WarningPercent = 11
        }));
        var service = new AiManagerService(settingsPath);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var missingRollout = Path.Combine(directory, "unavailable.jsonl");
            var runningRollout = Path.Combine(directory, "running.jsonl");
            File.WriteAllText(runningRollout,
                "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n");
            var server = Enumerable.Range(0, 12).Select(index => new AiThreadSummary
            {
                Id = $"task-{index:00}", ConversationTitle = $"Conversation {index}",
                LatestUserMessage = "Stale preview", Status = "completed", ModelName = "test-model",
                RolloutPath = missingRollout, UpdatedAt = now.AddMinutes(-index)
            }).ToList();
            server.Add(new AiThreadSummary
            {
                Id = "late-running", ConversationTitle = "Older running task", Status = "notLoaded",
                ModelName = "test-model", RolloutPath = runningRollout, UpdatedAt = now.AddHours(-2)
            });
            var local = Enumerable.Range(0, 40).Select(index => new AiThreadSummary
            {
                Id = index == 0 ? "task-00" : $"local-{index:00}",
                ConversationTitle = "First request", LatestUserMessage = "Latest request",
                Status = index == 39 ? "waitingOnUserInput" : "completed", ModelName = "test-model",
                RolloutPath = missingRollout, UpdatedAt = now.AddMinutes(-30 - index)
            }).ToList();

            // Another window saves the naming preference while this service retains unsaved policy edits.
            service.Settings.WarningPercent = 93;
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AiManagerSettings
            {
                TaskNameSource = TaskNameSources.LatestUserMessage,
                WarningPercent = 11
            }));
            var canonical = service.BuildTaskSummariesAsync(server, local).GetAwaiter().GetResult();
            Check(canonical.Count == 32 && canonical[0].Id == "local-39" && canonical[1].Id == "late-running",
                "canonical task selection enriches all merged candidates before limiting and prioritizes older waiting/running tasks");
            Check(canonical.Single(item => item.Id == "task-00").Title == "Latest request" &&
                  canonical.Count(item => item.Id == "task-00") == 1,
                "canonical task selection merges duplicate IDs and applies the shared latest-message preference");
            Check(service.Settings.TaskNameSource == TaskNameSources.LatestUserMessage &&
                  service.Settings.WarningPercent == 93,
                "task refresh reloads only the naming preference without replacing unsaved settings");

            VerifyLocalTaskSelection(service, directory, now);
            VerifyTaskSourceSelection();
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyLocalTaskSelection(AiManagerService service, string directory, DateTimeOffset now)
    {
        var sessions = Path.Combine(directory, "sessions");
        Directory.CreateDirectory(sessions);
        for (var index = 0; index < 41; index++)
        {
            var path = Path.Combine(sessions, $"task-{index:00}.jsonl");
            var boundary = index == 40 ? "task_started" : "task_complete";
            File.WriteAllText(path, $$$"""
                {"type":"session_meta","payload":{"id":"local-task-{{{index:00}}}","thread_source":"user"}}
                {"type":"event_msg","payload":{"type":"{{{boundary}}}"}}

                """);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-index).UtcDateTime);
        }
        var selected = service.DiscoverLocalTasks(sessions, 64, 32);
        Check(selected.Count == 32 && selected[0].Id == "local-task-40" && selected[0].Status == "inProgress",
            "local discovery sorts every scanned task by status before applying its result limit");
    }

    private static void VerifyTaskSourceSelection()
    {
        using var document = JsonDocument.Parse("""
            {"data":[
              {"id":"interactive","source":"appServer"},
              {"id":"custom","source":{"custom":"desktop"}},
              {"id":"unknown","source":"unknown"},
              {"id":"subagent","source":{"subAgent":"review"}},
              {"id":"legacy-subagent","source":"subAgentOther"}
            ]}
            """);
        var parse = typeof(AiManagerService).GetMethod("ParseThreads", BindingFlags.Static | BindingFlags.NonPublic)!;
        var selected = (List<AiThreadSummary>)parse.Invoke(null, new object[] { document.RootElement })!;
        Check(selected.Select(item => item.Id).SequenceEqual(new[] { "interactive", "custom", "unknown" }),
            "task parsing excludes recorded subagents while preserving interactive, custom, and unknown sources");
    }
}
