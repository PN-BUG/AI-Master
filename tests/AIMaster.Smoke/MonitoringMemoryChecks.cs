using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyMonitoringMemoryChecks()
    {
        VerifyBoundedServerMetadata();
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-bounded-cursors-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var service = new AiManagerService(Path.Combine(root, "settings.json"));
        try
        {
            for (var index = 0; index < 180; index++)
            {
                var path = Path.Combine(sessions, $"session-{index:D3}.jsonl");
                File.WriteAllText(path, MemoryMetadata($"bounded-{index}") + MemoryEvent("task_complete") + MemoryUsage(2));
                Check(service.ReadLocalTask(path, DateTime.UtcNow)?.Id == $"bounded-{index}",
                    "bounded repository reads a complete task before releasing its cursor");
            }
            var tasks = (ICollection)typeof(AiManagerService).GetField("_rolloutCursors", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
            Check(tasks.Count <= 128, "task cursor retention stays bounded across 180 conversations");
            var day = DateOnly.FromDateTime(DateTime.Today);
            Check(service.ReadLocalUsageSummary(sessions, day).TotalTokens == 360,
                "usage cache eviction preserves every session in the current summary");
            var usages = (ICollection)typeof(AiManagerService).GetField("_localUsageCursors", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var repeated = service.ReadLocalUsageSummary(sessions, day);
            var repeatedAllocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Check(usages.Count <= 128 && repeated.TotalTokens == 360,
                "bounded usage cursors can replay evicted files without duplicating or losing tokens");
            Console.WriteLine($"Unchanged 180-file summary allocated={repeatedAllocation} bytes");
            Check(repeatedAllocation < 1024 * 1024,
                "unchanged summaries reuse compact aggregates instead of thrashing the bounded parser cache");
            var firstPath = Path.Combine(sessions, "session-000.jsonl");
            File.AppendAllText(firstPath, MemoryEvent("task_started") + MemoryUsage(3));
            Check(service.ReadLocalTask(firstPath, DateTime.UtcNow)?.Status == "inProgress" &&
                  service.ReadLocalUsageSummary(sessions, day).TotalTokens == 363,
                "an evicted session resumes from source and includes new task state and token usage");
            Directory.Delete(sessions, recursive: true);
            var runtime = (AiManagerRuntime)typeof(AiManagerService).GetField("_runtime", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(service)!;
            Check(service.ReadLocalUsageSummary(sessions, day).TotalTokens == 0 && runtime.UsageSummaries.IsEmpty,
                "removing a complete sessions directory releases its compact summaries");
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(root, recursive: true);
        }
        Task.Run(VerifyPassiveMetadataReadAsync).GetAwaiter().GetResult();
    }

    private static void VerifyBoundedServerMetadata()
    {
        const int maximumCharacters = 32 * 1024;
        var longName = "x" + string.Concat(Enumerable.Repeat("🌙", maximumCharacters)) + " end";
        var longPreview = GiantMetadataPreview();
        const string shortName = "  Normal  conversation 🌙  ";
        const string shortPreview = "  ## My request: short preview  ";
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { id = "giant-metadata", name = longName, preview = longPreview, status = "idle" },
                new { id = "short-metadata", name = shortName, preview = shortPreview, status = "idle" }
            }
        }));
        var parse = typeof(AiManagerService).GetMethod("ParseThreads", BindingFlags.Static | BindingFlags.NonPublic)!;
        var tasks = (System.Collections.Generic.List<AiThreadSummary>)parse.Invoke(null, new object[] { document.RootElement })!;
        var large = tasks[0];
        Check(large.ConversationTitle is { Length: <= maximumCharacters } &&
              large.LatestUserMessage is { Length: <= maximumCharacters } && large.Title.Length <= maximumCharacters,
            "server task names, previews and display titles retain at most 32768 UTF16 characters");
        Check(large.ConversationTitle!.StartsWith("x🌙", StringComparison.Ordinal) &&
              large.ConversationTitle.EndsWith("🌙 end", StringComparison.Ordinal) &&
              large.ConversationTitle.Contains('…') && large.LatestUserMessage!.Contains('…') &&
              large.LatestUserMessage.EndsWith("Request tail 🌙", StringComparison.Ordinal),
            "bounded server metadata preserves both ends and visibly marks omitted text");
        Check(HasPairedMetadataSurrogates(large.ConversationTitle) &&
              HasPairedMetadataSurrogates(large.Title) && HasPairedMetadataSurrogates(large.LatestUserMessage),
            "server metadata truncation does not split UTF16 surrogate pairs");
        Check(TaskTitleFormatter.Clean(large.LatestUserMessage).StartsWith("Last actual request 🌙", StringComparison.Ordinal),
            "a last request marker inside giant transport context survives preview bounding");
        Check(tasks[1].ConversationTitle == shortName && tasks[1].LatestUserMessage == shortPreview &&
              tasks[1].Title == "Normal conversation 🌙",
            "short raw server metadata and ordinary title formatting remain unchanged");
    }

    private static string GiantMetadataPreview() => "# AGENTS.md instructions\n" + new string('c', 96 * 1024) +
        "\n## My request: obsolete request\n" + new string('c', 96 * 1024) +
        "\n## My request: Last actual request 🌙\n" + new string('r', 96 * 1024) + "\nRequest tail 🌙";

    private static bool HasPairedMetadataSurrogates(string? text)
    {
        if (text is null) return true;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index == text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }

    private static async Task VerifyPassiveMetadataReadAsync()
    {
        await WithConnectionFixtureAsync("metadata-only", async (_, directory) =>
        {
            await using var service = new AiManagerService(Path.Combine(directory, "settings.json"));
            File.WriteAllText(Path.Combine(directory, "metadata.jsonl"), MemoryMetadata("metadata-thread") +
                MemoryEvent("user_message", ",\"message\":\"Latest locally recorded input\"") + MemoryEvent("task_started"));
            service.Settings.TaskNameSource = TaskNameSources.LatestUserMessage;
            var tasks = await service.BuildTaskSummariesAsync([new AiThreadSummary
            {
                Id = "metadata-thread", Title = "Fallback", LatestUserMessage = "Stale list preview", Status = "completed"
            }], []);
            var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "thread-read.json")));
            using (request)
                Check(!request.RootElement.GetProperty("includeTurns").GetBoolean() &&
                      tasks.Count == 1 && tasks[0].ConversationTitle == "Metadata title" && tasks[0].ModelName == "metadata-model",
                    "passive monitoring fetches thread metadata without complete turn history");
            Check(tasks[0].LatestUserMessage == "Latest locally recorded input" && tasks[0].Title == "Latest locally recorded input",
                "metadata-only enrichment reads fresh user input from a newly discovered rollout path");
            File.Delete(Path.Combine(directory, "metadata.jsonl"));
            var bounded = await service.BuildTaskSummariesAsync([new AiThreadSummary
            {
                Id = "metadata-fallback", Title = "Fallback", LatestUserMessage = GiantMetadataPreview(), Status = "completed"
            }], []);
            Check(bounded[0].LatestUserMessage is { Length: <= 32768 } && bounded[0].Title.Length <= 32768 &&
                  bounded[0].Title.StartsWith("Last actual request 🌙", StringComparison.Ordinal) &&
                  bounded[0].LatestUserMessage!.Contains('…'),
                "metadata-only enrichment bounds retained fallback previews when a local rollout is unavailable");
        });
    }
}
