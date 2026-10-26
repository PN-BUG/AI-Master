using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyMemoryUsageChecks()
    {
        long completeRetained = 0, partialRetained = 0, truncatedRetained = 0, cachedAllocated = 0;
        WithMemoryFixture((service, sessions) =>
        {
            var path = Path.Combine(sessions, "bom-crlf.jsonl");
            File.WriteAllText(path, (MemoryMetadata("bom-thread") + MemoryEvent("task_complete") + MemoryUsage(9))
                .Replace("\n", "\r\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Check(service.ReadLocalTask(path, DateTime.UtcNow)?.Id == "bom-thread" &&
                  service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today)).TotalTokens == 9,
                "UTF8 BOM and CRLF preserve the first metadata record and subsequent token counts");
        });
        WithMemoryFixture((service, sessions) =>
        {
            var path = Path.Combine(sessions, "complete.jsonl");
            File.WriteAllText(path,
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"opaque\",\"data\":\"" +
                new string('x', 3 * 1024 * 1024) + "\"}}\n" +
                MemoryMetadata("complete-thread") + MemoryEvent("user_message", ",\"message\":\"修复内存 🌙\"") +
                MemoryEvent("task_started") + MemoryUsage(37) + MemoryEvent("task_complete"));
            var task = service.ReadLocalTask(path, DateTime.UtcNow);
            var usage = service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today));
            Check(task?.Id == "complete-thread" && task.Title == "修复内存 🌙" &&
                  task.Status == "completed" && task.ModelName == "memory-fixture-model" &&
                  usage.TotalTokens == 37 && usage.SessionCount == 1 && usage.Projects.Count == 1 &&
                  usage.MostUsedModel == "memory-fixture-model",
                "large opaque records preserve following UTF8 task metadata and exact token usage");
            completeRetained = MemoryPendingBytes(service, capacity: true);

            // Malformed large records must release their storage and leave the next record readable.
            File.AppendAllText(path, "{\"broken\":\"" + new string('x', 2 * 1024 * 1024) + "\"\n" + MemoryUsage(5));
            task = service.ReadLocalTask(path, DateTime.UtcNow);
            usage = service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today));
            Check(task?.Status == "completed" && task.Title == "修复内存 🌙" && usage.TotalTokens == 42,
                "malformed oversized records do not hide subsequent usage or overwrite task state");
            completeRetained = Math.Max(completeRetained, MemoryPendingBytes(service, capacity: true));
        });

        WithMemoryFixture((service, sessions) =>
        {
            var path = Path.Combine(sessions, "partial.jsonl");
            File.WriteAllText(path, MemoryMetadata("partial-thread") + MemoryEvent("task_started") + MemoryUsage(7));
            var pending = Encoding.UTF8.GetBytes(
                "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"追加后完成 🌙\"}],\"opaque\":\"" +
                new string('x', 2 * 1024 * 1024) + "汉");
            // Leave the last UTF8 codepoint split between scans, as a writer may do.
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
                stream.Write(pending, 0, pending.Length - 1);
            var task = service.ReadLocalTask(path, DateTime.UtcNow);
            var usage = service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today));
            Check(task?.Status == "inProgress" && task.LatestUserMessage is null && usage.TotalTokens == 7 &&
                  MemoryPendingBytes(service, capacity: false) < 256 * 1024,
                "incomplete large records keep bounded projected UTF8 state without premature parsing");
            service.ReadLocalTask(path, DateTime.UtcNow);
            Check(service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today)).TotalTokens == 7 &&
                  MemoryPendingBytes(service, capacity: false) < 256 * 1024,
                "unchanged incomplete records neither duplicate pending bytes nor token usage");
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
                stream.WriteByte(pending[^1]);
            File.AppendAllText(path, "\"}}\n" + MemoryEvent("task_complete") + MemoryUsage(11));
            task = service.ReadLocalTask(path, DateTime.UtcNow);
            usage = service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today));
            Check(task?.Status == "completed" && task.Title == "追加后完成 🌙" && usage.TotalTokens == 18 &&
                  MemoryPendingBytes(service, capacity: false) == 0,
                "appended completion consumes the whole large record and updates task and tokens exactly once");
            service.ReadLocalTask(path, DateTime.UtcNow);
            Check(service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today)).TotalTokens == 18,
                "completed large records are not counted again on an unchanged scan");
            partialRetained = MemoryPendingBytes(service, capacity: true);

            File.AppendAllText(path, "{\"unfinished\":\"" + new string('x', 2 * 1024 * 1024));
            service.ReadLocalTask(path, DateTime.UtcNow);
            service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today));
            File.WriteAllText(path, MemoryMetadata("rewritten-thread") +
                MemoryEvent("user_message", ",\"message\":\"重写后的任务\"") + MemoryEvent("task_started") + MemoryUsage(3));
            task = service.ReadLocalTask(path, DateTime.UtcNow);
            usage = service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today));
            Check(task?.Id == "rewritten-thread" && task.Title == "重写后的任务" && task.Status == "inProgress" &&
                  usage.TotalTokens == 3 && MemoryPendingBytes(service, capacity: false) == 0,
                "truncation discards incomplete bytes and resets prior task and usage state");
            truncatedRetained = MemoryPendingBytes(service, capacity: true);
        });

        WithMemoryFixture((service, sessions) =>
        {
            var paths = new string[20];
            for (var index = 0; index < paths.Length; index++)
            {
                paths[index] = Path.Combine(sessions, $"cached-{index}.jsonl");
                File.WriteAllText(paths[index], MemoryMetadata($"cached-{index}") + MemoryEvent("task_complete") + MemoryUsage(1));
                service.ReadLocalTask(paths[index], DateTime.UtcNow);
            }
            Check(service.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today)).TotalTokens == 20,
                "cached memory fixtures contain twenty independently counted usage records");
            var updatedAt = DateTime.UtcNow;
            foreach (var path in paths) service.ReadLocalTask(path, updatedAt);
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var path in paths) service.ReadLocalTask(path, updatedAt);
            cachedAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        Console.WriteLine($"Memory fixture metrics: complete retained={completeRetained}; append retained={partialRetained}; " +
                          $"truncate retained={truncatedRetained}; twenty cached task reads allocated={cachedAllocated} bytes");
        Check(completeRetained < 1024 * 1024,
            "completed or malformed multi-MiB lines retain less than 1MiB across task and usage cursors");
        Check(partialRetained < 1024 * 1024 && truncatedRetained < 1024 * 1024,
            "appended completion and truncation release oversized pending-line capacity");
        Check(cachedAllocated < 32 * 1024,
            "twenty unchanged cached task reads allocate less than 32KiB without per-file scan buffers");
    }

    private static void WithMemoryFixture(Action<AiManagerService, string> verify)
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-memory-check-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        AiManagerService? service = null;
        try
        {
            service = new AiManagerService(Path.Combine(root, "settings.json"));
            service.Settings.TaskNameSource = TaskNameSources.ConversationTitle;
            verify(service, sessions);
        }
        finally
        {
            try { service?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private static string MemoryMetadata(string id) =>
        "{\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"thread_source\":\"user\",\"cwd\":\"C:\\\\MemoryFixture\\\\测试\"}}\n" +
        "{\"type\":\"turn_context\",\"payload\":{\"model\":\"memory-fixture-model\"}}\n";

    private static string MemoryEvent(string type, string extra = "") =>
        "{\"type\":\"event_msg\",\"payload\":{\"type\":\"" + type + "\"" + extra + "}}\n";

    private static string MemoryUsage(int tokens) =>
        "{\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":" + tokens + "}}}\n";

    // Retained capacity is the regression itself; reflection keeps diagnostics out of production APIs.
    private static long MemoryPendingBytes(AiManagerService service, bool capacity)
    {
        long bytes = 0;
        foreach (var field in new[] { "_rolloutCursors", "_localUsageCursors" })
        {
            var cursors = typeof(AiManagerService).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service)!;
            var values = (IEnumerable)cursors.GetType().GetProperty("Values")!.GetValue(cursors)!;
            foreach (var cursor in values)
            {
                var pending = (RolloutRecordProjector)cursor!.GetType().GetField("PendingLine")!.GetValue(cursor)!;
                bytes += capacity ? pending.Capacity : pending.PendingLength;
            }
        }
        return bytes;
    }
}
