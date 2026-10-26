using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyRolloutActivityIndex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster-rollout-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            VerifyOpenRolloutActivity(directory);
            VerifyRolloutActivityDiscoveryBudget(directory);
            VerifyColdLargeRolloutActivity(directory);
            VerifyRolloutActivityRewrites(directory);
            VerifyRolloutActivityHeaderFallbacks(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void VerifyOpenRolloutActivity(string directory)
    {
        var root = Path.Combine(directory, "open-writer");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "active.jsonl");
        var stale = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var started = new DateTime(2026, 10, 25, 4, 0, 0, DateTimeKind.Utc);
        var completed = started.AddMinutes(1);
        var index = new RolloutActivityIndex();
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        writer.WriteLine(ActivityIndexRecord(started, "task_started"));
        File.SetLastWriteTimeUtc(path, stale);
        var first = index.GetCandidates(root).Single();
        Check(first.ActivityAtUtc == started && first.IsUserSession,
            "an open rollout uses its record timestamp rather than stale Windows write metadata");

        writer.WriteLine(ActivityIndexRecord(completed, "task_complete"));
        File.SetLastWriteTimeUtc(path, stale);
        var second = index.GetCandidates(root).Single();
        Check(second.Length > first.Length && second.ActivityAtUtc == completed,
            "an append advances cached rollout activity while the writer stays open and mtime stays unchanged");

        writer.Write("{\"timestamp\":\"2026-10-25T04:02:00Z\",\"padding\":\"");
        writer.Write(new string('x', 300 * 1024));
        writer.Flush();
        File.SetLastWriteTimeUtc(path, stale);
        Check(index.GetCandidates(root).Single().ActivityAtUtc == completed,
            "a partial line larger than the bounded tail preserves the previous complete record timestamp");
        writer.WriteLine("\"}");
        writer.WriteLine("not valid JSON");
        writer.Write("{\"timestamp\":\"2026-10-25T04:03:00Z\"");
        File.SetLastWriteTimeUtc(path, stale);
        Check(index.GetCandidates(root).Single().ActivityAtUtc == completed,
            "malformed and unterminated records cannot replace the last known valid activity");
        writer.WriteLine(",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}");
        File.SetLastWriteTimeUtc(path, stale);
        var repaired = index.GetCandidates(root).Single();
        Check(repaired.ActivityAtUtc == started.AddMinutes(3),
            "completing a previously partial JSONL record updates activity on the next cached scan");
        Check(index.GetCandidates(root).Single() == repaired,
            "an unchanged rollout retains the indexed activity between polling passes");
    }

    private static void VerifyRolloutActivityDiscoveryBudget(string directory)
    {
        var root = Path.Combine(directory, "discovery-budget");
        Directory.CreateDirectory(root);
        var current = new DateTime(2026, 10, 25, 5, 0, 0, DateTimeKind.Utc);
        var target = Path.Combine(root, "user.jsonl");
        File.WriteAllText(target, ActivityIndexRecord(current, "task_started") + "\n");
        File.SetLastWriteTimeUtc(target, current.AddDays(-3));
        for (var number = 0; number < 70; number++)
        {
            var path = Path.Combine(root, $"agent-{number:00}.jsonl");
            File.WriteAllText(path, ActivityIndexHeader("agent") + "\n" +
                                    ActivityIndexRecord(current.AddHours(-1), "task_started") + "\n");
            File.SetLastWriteTimeUtc(path, current.AddDays(1));
        }

        var index = new RolloutActivityIndex();
        var candidates = index.GetCandidates(root);
        Check(candidates.Count == 71 && candidates[0].Path == target,
            "more than 64 files with newer mtimes cannot hide the rollout with the newest record activity");

        foreach (var path in Directory.EnumerateFiles(root, "agent-*.jsonl"))
        {
            File.WriteAllText(path, ActivityIndexHeader("agent") + "\n" +
                                    ActivityIndexRecord(current.AddHours(1), "task_started") + "\n");
            File.SetLastWriteTimeUtc(path, current.AddDays(2));
        }
        candidates = index.GetCandidates(root);
        Check(candidates.Count(item => !item.IsUserSession) == 70 &&
              candidates.Where(item => item.IsUserSession).Take(64).Single().Path == target,
            "source classification can exclude 70 newer agent rollouts before the caller applies its read budget");
        var equalActivityPaths = candidates.Where(item => !item.IsUserSession).Select(item => item.Path).ToArray();
        Check(equalActivityPaths.SequenceEqual(equalActivityPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)),
            "equal activity timestamps have stable path ordering");
    }

    private static void VerifyRolloutActivityRewrites(string directory)
    {
        var root = Path.Combine(directory, "rewrites");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "rewritten.jsonl");
        var latest = new DateTime(2026, 10, 25, 6, 0, 0, DateTimeKind.Utc);
        var older = latest.AddDays(-1);
        var index = new RolloutActivityIndex();
        File.WriteAllText(path, ActivityIndexHeader("agent") + "\n" +
                                ActivityIndexRecord(latest, "task_complete") + "\n" + new string(' ', 1024));
        Check(!index.GetCandidates(root).Single().IsUserSession,
            "a valid non-user session header is classified before task parsing");
        File.WriteAllText(path, ActivityIndexRecord(older, "task_started") + "\n");
        File.SetLastWriteTimeUtc(path, latest.AddDays(-5));
        var truncated = index.GetCandidates(root).Single();
        Check(truncated.ActivityAtUtc == older && truncated.IsUserSession,
            "truncating a rollout discards cached timestamps and source metadata from the previous contents");

        File.WriteAllText(path, ActivityIndexRecord(latest, "task_started") + "\n");
        File.SetLastWriteTimeUtc(path, latest.AddHours(1));
        var rewritten = index.GetCandidates(root).Single();
        Check(rewritten.Length == truncated.Length && rewritten.ActivityAtUtc == latest,
            "mtime changes invalidate a same-length rewritten rollout");
        File.Delete(path);
        Check(index.GetCandidates(root).Count == 0, "deleted rollouts are removed from the activity index");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { index.GetCandidates(root, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "rollout activity discovery honors cancellation even for an empty directory");
    }

    private static void VerifyColdLargeRolloutActivity(string directory)
    {
        var root = Path.Combine(directory, "cold-large-records");
        Directory.CreateDirectory(root);
        var started = new DateTime(2026, 10, 25, 7, 0, 0, DateTimeKind.Utc);
        var stale = started.AddDays(-5);
        for (var number = 0; number < 70; number++)
        {
            var path = Path.Combine(root, $"recent-mtime-{number:00}.jsonl");
            File.WriteAllText(path, ActivityIndexRecord(started.AddMinutes(-1), "task_complete") + "\n");
            File.SetLastWriteTimeUtc(path, started.AddDays(1));
        }

        var completePath = Path.Combine(root, "large-complete.jsonl");
        var partialPath = Path.Combine(root, "large-partial.jsonl");
        using var completeStream = new FileStream(completePath, FileMode.Create, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        using var partialStream = new FileStream(partialPath, FileMode.Create, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        using var completeWriter = new StreamWriter(completeStream, new UTF8Encoding(false), 1024, leaveOpen: true)
            { AutoFlush = true };
        using var partialWriter = new StreamWriter(partialStream, new UTF8Encoding(false), 1024, leaveOpen: true)
            { AutoFlush = true };
        completeWriter.WriteLine(ActivityIndexRecord(started, "task_started"));
        partialWriter.WriteLine(ActivityIndexRecord(started, "task_started"));
        completeWriter.Write("{\"timestamp\":\"2026-10-25T07:01:00Z\",\"padding\":\"");
        partialWriter.Write("{\"timestamp\":\"2099-01-01T00:00:00Z\",\"padding\":\"");
        var padding = new string('x', 900 * 1024);
        completeWriter.Write(padding);
        completeWriter.WriteLine("\"}");
        partialWriter.Write(padding);
        partialWriter.Flush();
        File.SetLastWriteTimeUtc(completePath, stale);
        File.SetLastWriteTimeUtc(partialPath, stale);

        var index = new RolloutActivityIndex();
        var candidates = index.GetCandidates(root);
        Check(candidates.Single(item => item.Path == completePath).ActivityAtUtc == started.AddMinutes(1),
            "a cold index finds the top-level timestamp at the start of a complete record larger than its tail buffer");
        Check(candidates.Single(item => item.Path == partialPath).ActivityAtUtc == started,
            "a cold index skips an oversized unterminated record and finds the previous complete activity record");
        Check(candidates.Take(64).Any(item => item.Path == completePath) &&
              candidates.Take(64).Any(item => item.Path == partialPath),
            "oversized cold records with stale mtimes stay ahead of 70 newer-mtime user distractors");

        var nestedPath = Path.Combine(root, "large-nested-timestamp.jsonl");
        File.WriteAllText(nestedPath, ActivityIndexRecord(started, "task_started") + "\n" +
            "{\"payload\":{\"timestamp\":\"2099-01-01T00:00:00Z\"},\"padding\":\"" + padding + "\"}\n");
        File.SetLastWriteTimeUtc(nestedPath, stale);
        Check(new RolloutActivityIndex().GetCandidates(root).Single(item => item.Path == nestedPath).ActivityAtUtc == started,
            "oversized record recovery ignores nested timestamps and reads a previous top-level timestamp");
    }

    private static void VerifyRolloutActivityHeaderFallbacks(string directory)
    {
        var root = Path.Combine(directory, "header-fallbacks");
        Directory.CreateDirectory(root);
        var fallback = new DateTime(2026, 10, 23, 1, 0, 0, DateTimeKind.Utc);
        var lines = new[]
        {
            "{\"type\":\"session_meta\",\"payload\":{\"thread_source\":null}}\n",
            "{\"type\":\"session_meta\",\"payload\":{\"thread_source\":{\"kind\":\"agent\"}}}\n",
            "{\"type\":\"session_meta\",\"payload\":{\"thread_source\":\"user\"}}\n",
            "not valid JSON\n",
            "{\"timestamp\":\"bad timestamp\",\"payload\":{\"timestamp\":\"2099-01-01T00:00:00Z\"}}\n"
        };
        for (var number = 0; number < lines.Length; number++)
        {
            var path = Path.Combine(root, $"fallback-{number}.jsonl");
            File.WriteAllText(path, lines[number]);
            File.SetLastWriteTimeUtc(path, fallback);
        }
        var index = new RolloutActivityIndex();
        var candidates = index.GetCandidates(root);
        Check(candidates.Count == lines.Length && candidates.All(item => item.IsUserSession && item.ActivityAtUtc == fallback),
            "unknown headers remain eligible and missing valid top-level record timestamps fall back to file metadata");
        Check(index.GetCandidates(Path.Combine(root, "missing")).Count == 0,
            "a missing sessions directory returns no rollout candidates");
    }

    private static string ActivityIndexRecord(DateTime timestamp, string type) => JsonSerializer.Serialize(new
    {
        timestamp = timestamp.ToString("O"), type = "event_msg", payload = new { type }
    });

    private static string ActivityIndexHeader(string source) => JsonSerializer.Serialize(new
    {
        type = "session_meta", payload = new { thread_source = source }
    });
}
