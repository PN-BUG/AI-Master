using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AIMaster.Services;

internal static partial class Program
{
    private static async Task VerifyTaskMonitorTraceAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster-task-trace-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "task-monitor-trace.jsonl");
        var previousPath = Path.Combine(directory, "task-monitor-trace.previous.jsonl");
        await TaskMonitorTrace.StopAsync().ConfigureAwait(false);
        try
        {
            TaskMonitorTrace.Record("scan", detail: "count=1");
            Check(!Directory.Exists(directory), "task diagnostics perform no writes before explicitly starting");
            TaskMonitorTrace.Start(directory);
            TaskMonitorTrace.Record("parsed", "fixture-thread", "waitingOnUserInput", 41, "offset=123;length=456");
            await TaskMonitorTrace.StopAsync().ConfigureAwait(false);
            using (var line = JsonDocument.Parse(File.ReadAllLines(path).Single()))
            {
                var record = line.RootElement;
                var fields = record.EnumerateObject().Select(item => item.Name).ToHashSet();
                Check(fields.SetEquals(new[] { "atUtc", "timestamp", "frequency", "stage", "threadId", "status", "revision", "detail" }) &&
                      record.GetProperty("atUtc").GetDateTime().Kind == DateTimeKind.Utc &&
                      record.GetProperty("timestamp").GetInt64() > 0 &&
                      record.GetProperty("frequency").GetInt64() > 0 &&
                      record.GetProperty("threadId").GetString() == "fixture-thread" &&
                      record.GetProperty("status").GetString() == "waitingOnUserInput" &&
                      record.GetProperty("revision").GetInt64() == 41,
                    "task diagnostics flush UTC and monotonic timing with structural metadata only");
            }

            var beforeStoppedRecord = new FileInfo(path).Length;
            TaskMonitorTrace.Record("ui", "stopped-thread", "inProgress");
            await TaskMonitorTrace.StopAsync().ConfigureAwait(false);
            Check(new FileInfo(path).Length == beforeStoppedRecord,
                "task diagnostics ignore records after stopping and support repeated stop calls");

            TaskMonitorTrace.Start(directory);
            for (var index = 0; index < 6; index++)
            {
                TaskMonitorTrace.Record("watcher", "sample-thread", detail: "length=" + index);
                TaskMonitorTrace.Record("scan", detail: "count=" + index);
                TaskMonitorTrace.Record("feed", "sample-thread", "inProgress", index);
                TaskMonitorTrace.Record("ui", "sample-thread", "inProgress", index, "main");
                TaskMonitorTrace.Record("ui", "sample-thread", "inProgress", index, "floating");
            }
            TaskMonitorTrace.Record("feed", "sample-thread", "waitingOnUserInput", 100);
            await TaskMonitorTrace.StopAsync().ConfigureAwait(false);
            var sampled = File.ReadAllLines(path).Skip(1).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Check(sampled.Length == 6 &&
                      sampled.Count(item => item.RootElement.GetProperty("stage").GetString() == "watcher") == 1 &&
                      sampled.Count(item => item.RootElement.GetProperty("stage").GetString() == "scan") == 1 &&
                      sampled.Count(item => item.RootElement.GetProperty("stage").GetString() == "feed") == 2 &&
                      sampled.Count(item => item.RootElement.GetProperty("stage").GetString() == "ui") == 2,
                    "task diagnostics sample file activity and ignore revision-only feed and viewer updates");
            }
            finally { foreach (var item in sampled) item.Dispose(); }

            TaskMonitorTrace.Start(directory);
            var detail = "fileMetadata=" + new string('x', 900);
            for (var index = 0; index < 6000; index++)
                TaskMonitorTrace.Record("feed", "fixture-thread", index % 2 == 0 ? "inProgress" : "waitingOnUserInput", index, detail);
            await TaskMonitorTrace.StopAsync().ConfigureAwait(false);
            Check(File.Exists(previousPath) && new FileInfo(path).Length <= TaskMonitorTrace.MaximumLogBytes &&
                  new FileInfo(previousPath).Length <= TaskMonitorTrace.MaximumLogBytes &&
                  Directory.GetFiles(directory).Length == 2,
                "a large diagnostic burst keeps only two bounded log files");
            var records = File.ReadAllLines(previousPath).Concat(File.ReadAllLines(path)).ToArray();
            Check(records.Length > 0 && records.All(IsBoundedMetadata),
                "overflow and rotation retain complete JSON records with bounded metadata strings");

            // Restart immediately, before the preceding writer has drained; writers must remain serialized.
            TaskMonitorTrace.Start(directory);
            TaskMonitorTrace.Record("feed", "restart-one", "inProgress", 7000);
            var firstStop = TaskMonitorTrace.StopAsync();
            TaskMonitorTrace.Start(directory);
            TaskMonitorTrace.Record("ui", "restart-two", "waitingOnUserInput", 7001);
            await Task.WhenAll(firstStop, TaskMonitorTrace.StopAsync()).ConfigureAwait(false);
            var restarted = File.ReadAllLines(previousPath).Concat(File.ReadAllLines(path)).ToArray();
            Check(restarted.Any(line => line.Contains("restart-one", StringComparison.Ordinal)) &&
                  restarted.Any(line => line.Contains("restart-two", StringComparison.Ordinal)),
                "an immediate diagnostic restart drains the previous worker before opening the next log writer");
        }
        finally
        {
            await TaskMonitorTrace.StopAsync().ConfigureAwait(false);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        static bool IsBoundedMetadata(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.GetProperty("stage").GetString() is "watcher" or "scan" or "parsed" or "feed" or "ui" &&
                   (!root.TryGetProperty("detail", out var detail) || detail.GetString()!.Length <= 512);
        }
    }
}
