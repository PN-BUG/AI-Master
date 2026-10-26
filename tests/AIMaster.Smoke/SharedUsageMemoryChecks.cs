using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifySharedUsageMemoryChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-shared-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, DateTimeOffset.Now.Offset);
            VerifySharedUsageFailedSaveIsolation(root, now);

            // A JSON roundtrip duplicates every sample/key and builds full UTF16 ledger strings.
            // Updating one real event should instead allocate in proportion to dictionary copies.
            const int sampleCount = 5000;
            var state = new SharedUsageCounter.State();
            SharedUsageSample? first = null;
            for (var index = 0; index < sampleCount; index++)
            {
                var sample = new SharedUsageSample($"memory-{index:D4}-" + new string('s', 48),
                    DateOnly.FromDateTime(now.LocalDateTime), "memory-model-" + new string('m', 24),
                    100, now.AddSeconds(index - sampleCount));
                state.Samples[sample.Key] = sample;
                first ??= sample;
            }
            foreach (var mode in new[] { SharedUsageResetModes.Never, SharedUsageResetModes.Weekly, SharedUsageResetModes.AccountReset })
            {
                state.ModeBaselines[mode] = state.Samples.ToDictionary(item => item.Key, _ => 25L);
                state.ModeUndoBaselines[mode] = state.Samples.ToDictionary(item => item.Key, _ => 10L);
            }
            var path = Path.Combine(root, "large-ledger.json");
            File.WriteAllText(path, JsonSerializer.Serialize(state));
            var ledgerBytes = new FileInfo(path).Length;
            var counter = new SharedUsageCounter(path);
            counter.Update([first! with { Tokens = 101 }], SharedUsageResetModes.Never, now);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var pass = 0; pass < 3; pass++)
            {
                var summary = counter.Update([first! with { Tokens = 102 + pass }], SharedUsageResetModes.Never, now);
                Check(summary.TotalTokens == 375_002 + pass && summary.SessionCount == sampleCount,
                    "single-event ledger updates preserve all existing samples and cleared usage");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"Shared usage memory fixture: ledger={ledgerBytes} bytes; three updates allocated={allocated} bytes");
            Check(allocated < 36L * 1024 * 1024,
                "three single-event updates of a 5000-sample ledger allocate less than 36MiB");
            var reloaded = new SharedUsageCounter(path);
            Check(reloaded.Summarize(SharedUsageResetModes.Never, now).TotalTokens == 375_004 &&
                  reloaded.CanRestoreMode(SharedUsageResetModes.Never) &&
                  reloaded.CanRestoreMode(SharedUsageResetModes.Weekly) &&
                  reloaded.CanRestoreMode(SharedUsageResetModes.AccountReset),
                "streamed ledger saves reload with every independent undo baseline intact");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void VerifySharedUsageFailedSaveIsolation(string root, DateTimeOffset now)
    {
        var date = DateOnly.FromDateTime(now.LocalDateTime);
        var residual = new SharedUsageSample("recovered-session", date, "memory-model", 100, IsResidual: true);
        var recovered = residual with { Tokens = 60, Timestamp = now.AddHours(-2), IsResidual = false };
        var fresh = new SharedUsageSample("new-session", date, "memory-model", 5, now.AddMinutes(-30));
        var state = new SharedUsageCounter.State { LegacyObservedThrough = now.AddHours(-1) };
        state.Samples[residual.Key] = residual;
        state.ModeBaselines[SharedUsageResetModes.Never] = new() { [residual.Key] = 40 };
        state.ModeBaselines[SharedUsageResetModes.Weekly] = new() { [residual.Key] = 70 };
        state.ModeBaselines[SharedUsageResetModes.AccountReset] = new() { [residual.Key] = 90 };
        foreach (var mode in state.ModeBaselines.Keys)
            state.ModeUndoBaselines[mode] = new() { [residual.Key] = 10 };
        var limit = new AiLimitWindow { LimitId = "codex", WindowDurationMinutes = 10080, ResetsAt = now.AddDays(3) };
        state.PreviousLimits["codex:10080:False"] = limit;
        var path = Path.Combine(root, "isolation.json");
        var original = JsonSerializer.Serialize(state);
        File.WriteAllText(path, original);
        var counter = new SharedUsageCounter(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (var action in new[] { SharedUsageCounterAction.Clear, SharedUsageCounterAction.Restore })
            {
                var rejected = false;
                try
                {
                    counter.Update([recovered, fresh], SharedUsageResetModes.Never, now,
                        [new AiLimitWindow { LimitId = "codex", WindowDurationMinutes = 10080, ResetsAt = now.AddDays(7) }], action);
                }
                catch (IOException) { rejected = true; }
                catch (UnauthorizedAccessException) { rejected = true; }
                Check(rejected && counter.Summarize(SharedUsageResetModes.Never, now).TotalTokens == 60 &&
                      counter.Summarize(SharedUsageResetModes.Weekly, now).TotalTokens == 30 &&
                      counter.Summarize(SharedUsageResetModes.AccountReset, now).TotalTokens == 10 &&
                      state.ModeBaselines.Keys.All(counter.CanRestoreMode) && File.ReadAllText(path) == original,
                    $"failed {action} leaves residual samples, nested mode baselines, undo and saved limits unchanged");
            }
        }
        Check(counter.Update([recovered, fresh], SharedUsageResetModes.Never, now).TotalTokens == 65 &&
              counter.Update([], SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Restore).TotalTokens == 95 &&
              counter.Summarize(SharedUsageResetModes.Weekly, now).TotalTokens == 35 &&
              counter.Summarize(SharedUsageResetModes.AccountReset, now).TotalTokens == 15,
            "a later successful recovery and restore preserve other modes after failed saves");

        // Migration also clears the old mutable daily baseline, before the atomic save.
        var legacyPath = Path.Combine(root, "legacy-isolation.json");
        var legacy = new SharedUsageCounter.State
        {
            FormatVersion = 1,
            Samples = new() { [residual.Key] = residual with { IsResidual = false } },
            Baseline = new() { [residual.Key] = 40 },
            UndoBaseline = new() { [residual.Key] = 10 }
        };
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(legacy));
        var migrating = new SharedUsageCounter(legacyPath);
        using (var locked = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var rejected = false;
            try { migrating.Update([recovered], SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Clear); }
            catch (IOException) { rejected = true; }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected && migrating.RequiresTimestampMigration && migrating.CanRestore,
                "failed migration retains legacy version and undo state");
        }
        Check(migrating.Update([recovered], SharedUsageResetModes.Never, now).TotalTokens == 60 &&
              migrating.Update([], SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Restore).TotalTokens == 90,
            "failed migration preserves the original daily clear and undo amounts for retry");
    }
}
