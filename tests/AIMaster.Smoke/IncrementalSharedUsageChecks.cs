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
    private static void VerifyIncrementalSharedUsageChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-incremental-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, DateTimeOffset.Now.Offset);
            VerifyUnchangedLedgerDoesNotWrite(root, now);
            VerifyIncrementalLedgerChanges(root, now);
            VerifyIncrementalLedgerMigration(root, now);
            VerifyIncrementalSummarySemantics(root, now);
            VerifyLoadedLedgerStringSharing(root, now);
            VerifyIncrementalLedgerMemoryAndLoad(root, now);
            VerifySharedUsageFailedSaveIsolation(root, now);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AiLimitWindow IncrementalWeeklyLimit(DateTimeOffset reset, double used = 25,
        string name = "Codex") => new()
    {
        LimitId = "codex", Name = name, WindowDurationMinutes = 10080, ResetsAt = reset, UsedPercent = used
    };

    // A no-op must work even when both the output and temporary file cannot be replaced.
    // The timestamp also catches unnecessary writes on platforms with permissive file sharing.
    private static void WithLedgerWritesBlocked(string path, Action verify)
    {
        var sentinel = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, sentinel);
        using (var output = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var temporary = new FileStream(path + ".tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            verify();
        Check(File.GetLastWriteTimeUtc(path) == sentinel,
            "unchanged ledger refresh leaves the saved file timestamp untouched");
    }

    private static void VerifyUnchangedLedgerDoesNotWrite(string root, DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        var current = new SharedUsageSample("stable", date, "model", 40, now.AddMinutes(-1));
        var future = new SharedUsageSample("future", date, "model", 60, now.AddMinutes(1));
        var path = Path.Combine(root, "unchanged.json");
        var counter = new SharedUsageCounter(path);
        counter.Update([current, future], SharedUsageResetModes.Never, now,
            [IncrementalWeeklyLimit(now.AddHours(1))]);
        WithLedgerWritesBlocked(path, () =>
        {
            Check(counter.Update([current, current with { Tokens = 5 }, future, current with { Tokens = 0 }],
                    SharedUsageResetModes.Never, now, [IncrementalWeeklyLimit(now.AddHours(1))]).TotalTokens == 40,
                "equal, smaller, duplicate and zero samples with equivalent limit values are a read-only refresh");
            Check(counter.Update([], SharedUsageResetModes.Never, now.AddMinutes(2)).TotalTokens == 100,
                "a read-only refresh still includes a saved future event when its time arrives");
            Check(counter.Update([], SharedUsageResetModes.Weekly, now.AddDays(7)).TotalTokens == 0,
                "a read-only refresh recalculates the weekly boundary as time advances");
            Check(counter.Update([], SharedUsageResetModes.AccountReset, now.AddHours(2)).Error is not null,
                "a read-only refresh expires an official account boundary without writing the ledger");
            Check(counter.Update([], SharedUsageResetModes.Never, now,
                    action: SharedUsageCounterAction.Restore).TotalTokens == 40,
                "restore without an undo baseline does not write a ledger");
        });

        var emptyPath = Path.Combine(root, "empty", "no-ledger.json");
        var empty = new SharedUsageCounter(emptyPath);
        Check(empty.Update([], SharedUsageResetModes.Never, now).TotalTokens == 0 &&
              empty.Update([], SharedUsageResetModes.Never, now,
                  action: SharedUsageCounterAction.Restore).TotalTokens == 0 && !Directory.Exists(Path.GetDirectoryName(emptyPath)),
            "empty refresh and unavailable restore do not create a directory or ledger");
    }

    private static void VerifyIncrementalLedgerChanges(string root, DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        var sample = new SharedUsageSample("changing", date, "first-model", 40, now.AddMinutes(-1));
        var path = Path.Combine(root, "changing.json");
        var counter = new SharedUsageCounter(path);
        counter.Update([sample], SharedUsageResetModes.Never, now,
            [IncrementalWeeklyLimit(now.AddDays(2))]);
        Check(counter.Update([sample with { Tokens = 55 }, sample with { Tokens = 50 }],
                SharedUsageResetModes.Never, now).TotalTokens == 55,
            "a higher observation saves the maximum duplicate amount");
        var other = sample with { Model = "second-model", Tokens = 7 };
        var later = sample with { Timestamp = now, Tokens = 3 };
        var anotherSession = sample with { SessionId = "other-session", Tokens = 2 };
        Check(counter.Update([other, later, anotherSession], SharedUsageResetModes.Never, now).TotalTokens == 67 &&
              new SharedUsageCounter(path).Summarize(SharedUsageResetModes.Never, now).TotalTokens == 67,
            "with-expressions changing the model, timestamp or session create independently persisted sample keys");

        counter.Update([], SharedUsageResetModes.AccountReset, now,
            [IncrementalWeeklyLimit(now.AddDays(7), 10, "Updated quota")]);
        var state = JsonSerializer.Deserialize<SharedUsageCounter.State>(File.ReadAllText(path))!;
        Check(state.PreviousLimits["codex:10080:False"].UsedPercent == 10 &&
              state.PreviousLimits["codex:10080:False"].Name == "Updated quota" &&
              new SharedUsageCounter(path).Summarize(SharedUsageResetModes.AccountReset, now).TotalTokens == 3,
            "changed official quota values and reset boundaries persist even without new samples");
        Check(counter.Update([], SharedUsageResetModes.Never, now,
                action: SharedUsageCounterAction.Clear).TotalTokens == 0 && counter.CanRestore,
            "clear persists a changed baseline and its undo state");
        // The second clear deliberately replaces the first undo baseline. Only the third is a no-op.
        counter.Update([], SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Clear);
        WithLedgerWritesBlocked(path, () => Check(counter.Update([], SharedUsageResetModes.Never, now,
                action: SharedUsageCounterAction.Clear).TotalTokens == 0 && counter.CanRestore,
            "clear skips saving only when both the baseline and undo contents are already unchanged"));
        Check(counter.Update([], SharedUsageResetModes.Never, now,
                action: SharedUsageCounterAction.Restore).TotalTokens == 0 && !counter.CanRestore,
            "restore still removes undo state when restoring an equal baseline");
        WithLedgerWritesBlocked(path, () => Check(counter.Update([], SharedUsageResetModes.Never, now,
                action: SharedUsageCounterAction.Restore).TotalTokens == 0,
            "a repeated restore with no undo state is read-only"));
    }

    private static void VerifyIncrementalLedgerMigration(string root, DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        var daily = new SharedUsageSample("legacy", date, "legacy-model", 100);
        var path = Path.Combine(root, "missing-format-version.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Samples = new Dictionary<string, SharedUsageSample> { [daily.Key] = daily },
            Baseline = new Dictionary<string, long> { [daily.Key] = 40 },
            UndoBaseline = new Dictionary<string, long> { [daily.Key] = 10 },
            PreviousLimits = new Dictionary<string, AiLimitWindow>()
        }));
        File.SetLastWriteTimeUtc(path, now.UtcDateTime);
        var counter = new SharedUsageCounter(path);
        Check(counter.RequiresTimestampMigration && counter.Update([], SharedUsageResetModes.Never, now).TotalTokens == 60 &&
              !new SharedUsageCounter(path).RequiresTimestampMigration,
            "a stream-loaded missing format version migrates and persists even without incoming samples");
        WithLedgerWritesBlocked(path, () => Check(counter.Update([], SharedUsageResetModes.Never, now).TotalTokens == 60,
            "an unchanged legacy residual does not force a save on every refresh"));
        var recovered = daily with { Timestamp = now.AddHours(-1), Tokens = 60 };
        Check(counter.Update([recovered], SharedUsageResetModes.Never, now).TotalTokens == 60 &&
              new SharedUsageCounter(path).Update([], SharedUsageResetModes.Never, now,
                  action: SharedUsageCounterAction.Restore).TotalTokens == 90,
            "recovering an old event reconciles residual and undo amounts without double counting");

        foreach (var damaged in new[]
                 {
                     "null", "{broken", "{\"Samples\":null}", "{\"Baseline\":null}",
                     "{\"ModeBaselines\":null}", "{\"ModeUndoBaselines\":null}", "{\"PreviousLimits\":null}"
                 })
        {
            var damagedPath = Path.Combine(root, "damaged.json");
            File.WriteAllText(damagedPath, damaged);
            var rejected = false;
            try { _ = new SharedUsageCounter(damagedPath); }
            catch (JsonException) { rejected = true; }
            Check(rejected, "stream-loaded corrupt or null ledger state is rejected");
        }
    }

    private static void VerifyIncrementalSummarySemantics(string root, DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        var samples = new[]
        {
            new SharedUsageSample("tie-first", date, "first-model", 10, now.AddMinutes(-3)),
            new SharedUsageSample("tie-second", date, "second-model", 20, now.AddMinutes(-2)),
            new SharedUsageSample("tie-third", date, "first-model", 10, now.AddMinutes(-1)),
            new SharedUsageSample("cleared", date, "cleared-model", 999, now.AddMinutes(-4)),
            new SharedUsageSample("future", date, "future-model", 1000, now.AddMinutes(1)),
            new SharedUsageSample("before-week", date.AddDays(-1), "old-model", 1000, now.AddDays(-1))
        };
        var state = new SharedUsageCounter.State { Samples = samples.ToDictionary(item => item.Key) };
        state.ModeBaselines[SharedUsageResetModes.Weekly] = new() { [samples[3].Key] = 999 };
        var path = Path.Combine(root, "summary-semantics.json");
        File.WriteAllText(path, JsonSerializer.Serialize(state));
        var summary = new SharedUsageCounter(path).Summarize(SharedUsageResetModes.Weekly, now);
        Check(summary.TotalTokens == 40 && summary.SessionCount == 3 && summary.MostUsedModel == "first-model",
            "summary model ties follow the first counted encounter while cleared, old and future records stay excluded");

        var unknown = new SharedUsageSample("unknown-time", date, "unknown-model", 1);
        state.Samples[unknown.Key] = unknown;
        state.PreviousLimits["codex:10080:False"] = IncrementalWeeklyLimit(now.AddDays(7).AddHours(-1));
        File.WriteAllText(path, JsonSerializer.Serialize(state));
        summary = new SharedUsageCounter(path).Summarize(SharedUsageResetModes.AccountReset, now);
        Check(summary.Error is not null && summary.TotalTokens == 0 && summary.SessionCount == 0 && summary.MostUsedModel is null,
            "an ambiguous intraday record encountered after valid records suppresses every partial summary value");
        state.ModeBaselines[SharedUsageResetModes.AccountReset] = new() { [unknown.Key] = 1 };
        File.WriteAllText(path, JsonSerializer.Serialize(state));
        summary = new SharedUsageCounter(path).Summarize(SharedUsageResetModes.AccountReset, now);
        Check(summary.Error is null && summary.TotalTokens == 1039,
            "a fully cleared timestamp-free record cannot make the account boundary ambiguous");
    }

    private static void VerifyLoadedLedgerStringSharing(string root, DateTimeOffset now)
    {
        var state = new SharedUsageCounter.State();
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        for (var index = 0; index < 40; index++)
        {
            // Distinct strings with equal ordinal values become independent JSON strings
            // on reload even when all callers originally reused the same instance.
            var session = index % 2 == 0 ? "repeat-session" : "Repeat-session";
            var model = (index % 4) switch { 0 => "repeat-model", 1 => "Repeat-model", 2 => "", _ => null };
            var sample = new SharedUsageSample(new string(session.ToCharArray()), date,
                model is null ? null : new string(model.ToCharArray()), index + 10, now.AddMinutes(index - 60));
            state.Samples[sample.Key] = sample;
        }
        var sampleKeys = state.Samples.Keys.ToArray();
        state.Baseline = sampleKeys.ToDictionary(key => new string(key.ToCharArray()), _ => 2L);
        state.UndoBaseline = sampleKeys.ToDictionary(key => new string(key.ToCharArray()), _ => 1L);
        foreach (var mode in new[] { SharedUsageResetModes.Never, SharedUsageResetModes.Weekly, SharedUsageResetModes.AccountReset })
        {
            state.ModeBaselines[mode] = sampleKeys.ToDictionary(key => new string(key.ToCharArray()), _ => 3L);
            state.ModeUndoBaselines[mode] = sampleKeys.ToDictionary(key => new string(key.ToCharArray()), _ => 1L);
        }
        // Old/deleted source keys are valid saved offsets and must survive compaction.
        foreach (var baseline in state.ModeBaselines.Values.Concat(state.ModeUndoBaselines.Values))
            baseline[new string("orphaned-source-key".ToCharArray())] = 7;
        var path = Path.Combine(root, "repeated-ledger-strings.json");
        var originalJson = JsonSerializer.Serialize(state);
        File.WriteAllText(path, originalJson);
        var counter = new SharedUsageCounter(path);
        var loaded = (SharedUsageCounter.State)typeof(SharedUsageCounter)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(counter)!;
        Check(loaded.Samples.Values.GroupBy(sample => sample.SessionId, StringComparer.Ordinal)
                .All(group => group.All(sample => ReferenceEquals(group.First().SessionId, sample.SessionId))) &&
              loaded.Samples.Values.Where(sample => sample.Model is not null).GroupBy(sample => sample.Model!, StringComparer.Ordinal)
                .All(group => group.All(sample => ReferenceEquals(group.First().Model, sample.Model))),
            "loaded ledger shares equal session and model strings while preserving ordinal distinctions and null models");
        var canonicalKeys = loaded.Samples.Keys.ToDictionary(key => key, key => key, StringComparer.Ordinal);
        var baselines = new[] { loaded.Baseline, loaded.UndoBaseline! }
            .Concat(loaded.ModeBaselines.Values).Concat(loaded.ModeUndoBaselines.Values).ToArray();
        Check(baselines.SelectMany(baseline => baseline.Keys).All(key =>
                !canonicalKeys.TryGetValue(key, out var canonical) || ReferenceEquals(canonical, key)) &&
              baselines.SelectMany(baseline => baseline.Keys).Where(key => key == "orphaned-source-key")
                .Distinct(ReferenceEqualityComparer.Instance).Count() == 1,
            "loaded baseline and undo dictionaries share sample keys and equal orphan keys across every reset mode");
        Check(baselines.Distinct(ReferenceEqualityComparer.Instance).Count() == baselines.Length,
            "compacted baseline and undo dictionaries remain independently mutable");
        Check(JsonSerializer.Serialize(loaded) == originalJson && File.ReadAllText(path) == originalJson &&
              counter.Summarize(SharedUsageResetModes.Never, now).TotalTokens == state.Samples.Values.Sum(sample => sample.Tokens - 3),
            "ledger compaction preserves every persisted value, key order, summary and original file without a rewrite");

        var expanded = JsonSerializer.Deserialize<SharedUsageCounter.State>(originalJson)!;
        static int StringReferences(SharedUsageCounter.State value) => value.Samples.Keys
            .Concat(value.Samples.Values.SelectMany(sample => new[] { sample.SessionId, sample.Model }))
            .Concat(value.Baseline.Keys).Concat(value.UndoBaseline!.Keys)
            .Concat(value.ModeBaselines.Values.SelectMany(baseline => baseline.Keys))
            .Concat(value.ModeUndoBaselines.Values.SelectMany(baseline => baseline.Keys))
            .Where(text => text is not null).Distinct(ReferenceEqualityComparer.Instance).Count();
        Console.WriteLine($"Ledger strings: retained references {StringReferences(expanded)} -> {StringReferences(loaded)}");
    }

    private static void VerifyIncrementalLedgerMemoryAndLoad(string root, DateTimeOffset now)
    {
        const int sampleCount = 5000;
        var state = new SharedUsageCounter.State();
        var samples = new SharedUsageSample[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = new SharedUsageSample("incremental-" + index + new string('s', 48),
                DateOnly.FromDateTime(now.LocalDateTime), "model-" + new string('m', 24),
                100, now.AddSeconds(index - sampleCount));
            samples[index] = sample;
            state.Samples[sample.Key] = sample;
        }
        foreach (var mode in new[] { SharedUsageResetModes.Never, SharedUsageResetModes.Weekly, SharedUsageResetModes.AccountReset })
        {
            state.ModeBaselines[mode] = state.Samples.ToDictionary(item => item.Key, _ => 25L);
            state.ModeUndoBaselines[mode] = state.Samples.ToDictionary(item => item.Key, _ => 10L);
        }
        var path = Path.Combine(root, "large-incremental.json");
        using (var stream = File.Create(path)) JsonSerializer.Serialize(stream, state);
        var counter = new SharedUsageCounter(path);
        counter.Update(samples, SharedUsageResetModes.Never, now);
        counter.Summarize(SharedUsageResetModes.Never, now);
        var beforeSummary = GC.GetAllocatedBytesForCurrentThread();
        for (var pass = 0; pass < 3; pass++) counter.Summarize(SharedUsageResetModes.Never, now);
        var summaryAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeSummary;
        Console.WriteLine($"Incremental ledger: three 5000-sample summaries={summaryAllocation} allocated bytes");
        Check(summaryAllocation < 1536L * 1024,
            "three summaries of a 5000-sample ledger allocate less than 1.5MiB");
        var beforeRefresh = GC.GetAllocatedBytesForCurrentThread();
        for (var pass = 0; pass < 3; pass++)
            Check(counter.Update(samples, SharedUsageResetModes.Never, now).TotalTokens == 375_000,
                "unchanged large-ledger refresh preserves all samples and independent baselines");
        var refreshAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeRefresh;
        Console.WriteLine($"Incremental ledger: three summaries={summaryAllocation}; three unchanged refreshes={refreshAllocation} allocated bytes");
        Check(refreshAllocation - summaryAllocation < 6L * 1024 * 1024,
            "three unchanged full-snapshot refreshes add less than 6MiB above the required summaries");
        var beforeEmptyRefresh = GC.GetAllocatedBytesForCurrentThread();
        for (var pass = 0; pass < 3; pass++)
            Check(counter.Update([], SharedUsageResetModes.Never, now).TotalTokens == 375_000,
                "an unchanged scan delta still summarizes the complete saved ledger");
        var emptyRefreshAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeEmptyRefresh;
        Console.WriteLine($"Incremental ledger: three empty-delta refreshes={emptyRefreshAllocation} allocated bytes");
        Check(emptyRefreshAllocation - summaryAllocation < 128L * 1024,
            "three empty-delta refreshes add less than 128KiB above the required summaries");

        // Compare against a direct UTF8 stream load of the identical state rather than a
        // machine-dependent absolute budget. Compaction adds a temporary ordinal set and
        // replacement immutable records (about 720KiB/load for this fixture), while a full
        // UTF16 text or second DOM would add tens of megabytes across the three loads.
        using (var stream = File.OpenRead(path)) _ = JsonSerializer.Deserialize<SharedUsageCounter.State>(stream);
        _ = new SharedUsageCounter(path);
        var beforeStream = GC.GetAllocatedBytesForCurrentThread();
        for (var pass = 0; pass < 3; pass++)
        {
            using var stream = File.OpenRead(path);
            Check(JsonSerializer.Deserialize<SharedUsageCounter.State>(stream)!.Samples.Count == sampleCount,
                "the constructor allocation comparison uses a complete streamed ledger fixture");
        }
        var streamAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeStream;
        var beforeConstructor = GC.GetAllocatedBytesForCurrentThread();
        for (var pass = 0; pass < 3; pass++)
            Check(!new SharedUsageCounter(path).RequiresTimestampMigration, "explicit format version survives streamed constructor load");
        var constructorAllocation = GC.GetAllocatedBytesForCurrentThread() - beforeConstructor;
        Console.WriteLine($"Ledger load: three stream loads={streamAllocation}; three constructors={constructorAllocation} allocated bytes");
        Check(constructorAllocation - streamAllocation < 3L * 1024 * 1024,
            "three compacting constructors add less than 3MiB above direct UTF8 deserialization");
    }
}
