using System.Buffers;
using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

internal sealed record SharedUsageSample(string SessionId, DateOnly Date, string? Model, long Tokens,
    DateTimeOffset? Timestamp = null, bool IsResidual = false)
{
    public string Key => Timestamp is { } at
        ? $"{SessionId}:{Model}:{at.UtcTicks}"
        : $"{SessionId}:{Date:yyyy-MM-dd}:{Model}";
    public DateTimeOffset OccurredAt => Timestamp ?? new DateTimeOffset(Date.ToDateTime(TimeOnly.MinValue));
}

internal enum SharedUsageCounterAction { None, Clear, Restore }

/// <summary>A device-local ledger. Clearing changes a baseline, never the source logs or other devices.</summary>
internal sealed class SharedUsageCounter
{
    internal sealed class State
    {
        private int _formatVersion = 2;
        public int FormatVersion
        {
            get => _formatVersion;
            set { _formatVersion = value; HasExplicitFormatVersion = true; }
        }
        // Internal properties are not JSON members. Track presence during the same streamed
        // deserialize that loads the ledger, while retaining version 2 for newly created state.
        internal bool HasExplicitFormatVersion { get; private set; }
        public DateTimeOffset? LegacyObservedThrough { get; set; }
        public Dictionary<string, SharedUsageSample> Samples { get; set; } = new();
        // Version 1 used a single daily baseline. Kept only to migrate existing local state.
        public Dictionary<string, long> Baseline { get; set; } = new();
        public Dictionary<string, long>? UndoBaseline { get; set; }
        public Dictionary<string, Dictionary<string, long>> ModeBaselines { get; set; } = new();
        public Dictionary<string, Dictionary<string, long>> ModeUndoBaselines { get; set; } = new();
        public Dictionary<string, AiLimitWindow> PreviousLimits { get; set; } = new();
    }

    private readonly string _path;
    private readonly string _legacyMode;
    private State _state;
    public bool CanRestore => _state.UndoBaseline is not null || _state.ModeUndoBaselines.Count > 0;
    public bool CanRestoreMode(string mode) => _state.UndoBaseline is not null && mode == _legacyMode ||
        _state.ModeUndoBaselines.ContainsKey(SharedUsageResetModes.Normalize(mode));
    public bool RequiresTimestampMigration => _state.FormatVersion < 2;

    public SharedUsageCounter(string path, string legacyMode = SharedUsageResetModes.Never)
    {
        _path = path;
        _legacyMode = SharedUsageResetModes.Normalize(legacyMode);
        // A damaged state must not silently resurrect cleared usage or lose saved totals.
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            _state = JsonSerializer.Deserialize<State>(stream) ?? throw new JsonException("Invalid shared usage counter state");
            if (!_state.HasExplicitFormatVersion)
            {
                _state.FormatVersion = 1;
                _state.LegacyObservedThrough = File.GetLastWriteTimeUtc(path);
            }
        }
        else _state = new State();
        if (_state.Samples is null || _state.Baseline is null || _state.PreviousLimits is null ||
            _state.ModeBaselines is null || _state.ModeUndoBaselines is null)
            throw new JsonException("Invalid shared usage counter state");
        CompactLoadedStrings(_state);
    }

    private static void CompactLoadedStrings(State state)
    {
        // JSON repeats the session/model on every event and serializes clear offsets as
        // separate dictionaries. Reuse their equal ordinal strings after loading; keep
        // this set local so it cannot retain deleted history or grow with later refreshes.
        var strings = new HashSet<string>(state.Samples.Count, StringComparer.Ordinal);
        foreach (var key in state.Samples.Keys) strings.Add(key);
        foreach (var (key, sample) in state.Samples)
        {
            if (sample is null) continue;
            var session = CanonicalString(sample.SessionId, strings);
            var model = sample.Model is { } value ? CanonicalString(value, strings) : null;
            if (!ReferenceEquals(session, sample.SessionId) || !ReferenceEquals(model, sample.Model))
                // Updating an existing dictionary value leaves its keys, comparer and
                // enumeration order intact on the supported .NET 8 runtime.
                state.Samples[key] = sample with { SessionId = session, Model = model };
        }
        CompactBaselineKeys(state.Baseline, strings);
        if (state.UndoBaseline is { } undo) CompactBaselineKeys(undo, strings);
        foreach (var baseline in state.ModeBaselines.Values) CompactBaselineKeys(baseline, strings);
        foreach (var baseline in state.ModeUndoBaselines.Values) CompactBaselineKeys(baseline, strings);
    }

    private static string CanonicalString(string value, HashSet<string> strings)
    {
        // Preserve a malformed null value for the existing validation/usage behavior.
        if (value is null) return value!;
        if (strings.TryGetValue(value, out var canonical)) return canonical;
        strings.Add(value);
        return value;
    }

    private static void CompactBaselineKeys(Dictionary<string, long> baseline, HashSet<string> strings)
    {
        if (baseline is null || baseline.Count == 0) return;
        var entries = ArrayPool<KeyValuePair<string, long>>.Shared.Rent(baseline.Count);
        var count = 0;
        var changed = false;
        try
        {
            foreach (var (key, tokens) in baseline)
            {
                var canonical = CanonicalString(key, strings);
                entries[count++] = new(canonical, tokens);
                changed |= !ReferenceEquals(key, canonical);
            }
            if (!changed) return;
            // Refill the same dictionary to retain its comparer and allocated storage.
            // Copying in enumeration order also preserves the persisted property order.
            baseline.Clear();
            for (var index = 0; index < count; index++)
                baseline.Add(entries[index].Key, entries[index].Value);
        }
        finally
        {
            // The shared scratch array must never keep ledger strings alive.
            ArrayPool<KeyValuePair<string, long>>.Shared.Return(entries, clearArray: true);
        }
    }

    public AiLocalUsageSummary Update(IEnumerable<SharedUsageSample> samples, string mode,
        DateTimeOffset now, IEnumerable<AiLimitWindow>? limits = null,
        SharedUsageCounterAction action = SharedUsageCounterAction.None)
    {
        mode = SharedUsageResetModes.Normalize(mode);
        // Materialize an input only once, but postpone grouping and copying the saved ledger
        // until an observation can actually change it. Normal scans often supply no delta.
        var incoming = new List<SharedUsageSample>(samples.TryGetNonEnumeratedCount(out var count) ? count : 0);
        var changed = _state.FormatVersion < 2;
        foreach (var sample in samples)
        {
            if (sample.Tokens <= 0) continue;
            incoming.Add(sample);
            if (!_state.Samples.TryGetValue(sample.Key, out var old) || sample.Tokens > old.Tokens)
                changed = true;
        }
        Dictionary<string, AiLimitWindow>? incomingLimits = null;
        foreach (var limit in limits ?? [])
        {
            if (!limit.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                limit.WindowDurationMinutes is not (>= 6 * 24 * 60 and <= 8 * 24 * 60) ||
                limit.ResetsAt is null || !double.IsFinite(limit.UsedPercent) ||
                limit.UsedPercent is not (>= 0 and <= 100)) continue;
            var key = $"{limit.LimitId}:{limit.WindowDurationMinutes}:{limit.IsPrimary}";
            (incomingLimits ??= new())[key] = limit;
        }
        if (incomingLimits is not null)
            foreach (var (key, limit) in incomingLimits)
                if (!_state.PreviousLimits.TryGetValue(key, out var old) || !SameLimit(old, limit)) changed = true;
        if (!changed && !ActionChangesState(_state, mode, now, action))
            return Summarize(mode, now);

        // Copy every mutable ledger dictionary so a failed save cannot change live state.
        // Samples and limit windows are immutable and can be shared without a JSON roundtrip.
        var next = new State
        {
            FormatVersion = _state.FormatVersion,
            LegacyObservedThrough = _state.LegacyObservedThrough,
            Samples = new(_state.Samples, _state.Samples.Comparer),
            Baseline = new(_state.Baseline, _state.Baseline.Comparer),
            UndoBaseline = _state.UndoBaseline is { } undoBaseline
                ? new(undoBaseline, undoBaseline.Comparer) : null,
            ModeBaselines = _state.ModeBaselines.ToDictionary(item => item.Key,
                item => new Dictionary<string, long>(item.Value, item.Value.Comparer), _state.ModeBaselines.Comparer),
            ModeUndoBaselines = _state.ModeUndoBaselines.ToDictionary(item => item.Key,
                item => new Dictionary<string, long>(item.Value, item.Value.Comparer), _state.ModeUndoBaselines.Comparer),
            PreviousLimits = new(_state.PreviousLimits, _state.PreviousLimits.Comparer)
        };
        incoming = incoming.GroupBy(item => item.Key)
            .Select(group => group.MaxBy(item => item.Tokens)!).ToList();
        if (next.FormatVersion < 2) MigrateDailyState(next, incoming, _legacyMode);
        ReconcileDailyResiduals(next, incoming);
        foreach (var sample in incoming)
        {
            // Retain observations across log deletion/truncation and avoid recounting after restart.
            if (!next.Samples.TryGetValue(sample.Key, out var old) || sample.Tokens > old.Tokens)
                next.Samples[sample.Key] = sample;
        }

        if (incomingLimits is not null)
            foreach (var (key, limit) in incomingLimits) next.PreviousLimits[key] = limit;

        var baseline = next.ModeBaselines.GetValueOrDefault(mode) ?? new();
        if (action == SharedUsageCounterAction.Clear)
        {
            next.ModeUndoBaselines[mode] = new(baseline);
            next.ModeBaselines[mode] = next.Samples.Where(item => item.Value.OccurredAt <= now)
                .ToDictionary(item => item.Key, item => item.Value.Tokens);
        }
        else if (action == SharedUsageCounterAction.Restore && next.ModeUndoBaselines.Remove(mode, out var undo))
        {
            next.ModeBaselines[mode] = undo;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, next);
        File.Move(temporary, _path, overwrite: true);
        _state = next;
        return Summarize(mode, now);
    }

    private static bool SameLimit(AiLimitWindow left, AiLimitWindow right) =>
        left.LimitId == right.LimitId && left.Name == right.Name && left.UsedPercent == right.UsedPercent &&
        left.ResetsAt == right.ResetsAt && left.WindowDurationMinutes == right.WindowDurationMinutes &&
        left.IsPrimary == right.IsPrimary;

    private static bool ActionChangesState(State state, string mode, DateTimeOffset now,
        SharedUsageCounterAction action)
    {
        if (action == SharedUsageCounterAction.Restore) return state.ModeUndoBaselines.ContainsKey(mode);
        if (action != SharedUsageCounterAction.Clear) return false;
        if (!state.ModeBaselines.TryGetValue(mode, out var baseline) ||
            !state.ModeUndoBaselines.TryGetValue(mode, out var undo) || baseline.Count != undo.Count)
            return true;
        foreach (var (key, tokens) in baseline)
            if (!undo.TryGetValue(key, out var previous) || previous != tokens) return true;
        var count = 0;
        foreach (var (key, sample) in state.Samples)
        {
            if (sample.OccurredAt > now) continue;
            count++;
            if (!baseline.TryGetValue(key, out var cleared) || cleared != sample.Tokens) return true;
        }
        return count != baseline.Count;
    }

    private static void MigrateDailyState(State state, List<SharedUsageSample> incoming, string mode)
    {
        // Split old daily observations into timestamped events, distributing saved clear offsets
        // chronologically. Unreadable/deleted history remains as a daily residual instead of disappearing.
        var baseline = new Dictionary<string, long>(state.Baseline);
        var undo = state.UndoBaseline is { } oldUndo ? new Dictionary<string, long>(oldUndo) : null;
        foreach (var old in state.Samples.Values.ToList())
        {
            var replacements = incoming.Where(item => item.SessionId == old.SessionId &&
                item.Date == old.Date && item.Model == old.Model).ToList();
            if (replacements.Count == 0)
            {
                state.Samples[old.Key] = old with { IsResidual = true };
                continue;
            }
            var residual = Math.Max(0, old.Tokens - replacements.Sum(item => item.Tokens));
            var unknown = replacements.FindIndex(item => item.Timestamp is null);
            if (residual > 0)
            {
                if (unknown >= 0) replacements[unknown] = replacements[unknown] with { Tokens = replacements[unknown].Tokens + residual, IsResidual = true };
                else replacements.Add(old with { Tokens = residual, IsResidual = true });
            }
            state.Samples.Remove(old.Key);
            foreach (var replacement in replacements) state.Samples[replacement.Key] = replacement;
            SplitBaseline(baseline, old.Key, replacements);
            if (undo is not null) SplitBaseline(undo, old.Key, replacements);
        }
        state.ModeBaselines[mode] = baseline;
        if (undo is not null) state.ModeUndoBaselines[mode] = undo;
        state.Baseline.Clear();
        state.UndoBaseline = null;
        state.FormatVersion = 2;
    }

    private static void ReconcileDailyResiduals(State state, List<SharedUsageSample> incoming)
    {
        if (state.LegacyObservedThrough is not { } observedThrough) return;
        foreach (var residual in state.Samples.Values.Where(item => item.IsResidual).ToList())
        {
            // A previously unavailable old log can return later. Replace its unknown daily
            // amount with newly recovered old events; genuinely newer events remain additive.
            var recovered = incoming.Where(item => item.Timestamp is { } at && at <= observedThrough &&
                    item.SessionId == residual.SessionId && item.Date == residual.Date && item.Model == residual.Model)
                .Select(item => item with { Tokens = Math.Max(0, item.Tokens - (state.Samples.GetValueOrDefault(item.Key)?.Tokens ?? 0)) })
                .Where(item => item.Tokens > 0).OrderBy(item => item.OccurredAt).ToList();
            var replaced = Math.Min(residual.Tokens, recovered.Sum(item => item.Tokens));
            if (replaced == 0) continue;
            foreach (var baseline in state.ModeBaselines.Values.Concat(state.ModeUndoBaselines.Values))
            {
                var cleared = Math.Min(replaced, baseline.GetValueOrDefault(residual.Key));
                baseline[residual.Key] = baseline.GetValueOrDefault(residual.Key) - cleared;
                foreach (var sample in recovered)
                {
                    var amount = Math.Min(cleared, sample.Tokens);
                    if (amount > 0) baseline[sample.Key] = baseline.GetValueOrDefault(sample.Key) + amount;
                    cleared -= amount;
                }
            }
            if (residual.Tokens == replaced) state.Samples.Remove(residual.Key);
            else state.Samples[residual.Key] = residual with { Tokens = residual.Tokens - replaced };
        }
    }

    private static void SplitBaseline(Dictionary<string, long> baseline, string oldKey, List<SharedUsageSample> replacements)
    {
        if (!baseline.Remove(oldKey, out var remaining)) return;
        foreach (var item in replacements.OrderBy(item => item.OccurredAt))
        {
            var cleared = Math.Min(remaining, item.Tokens);
            if (cleared > 0) baseline[item.Key] = cleared;
            remaining -= cleared;
        }
    }

    private DateTimeOffset? AccountPeriodStart(DateTimeOffset now) => _state.PreviousLimits.Values
        .Where(item => item.ResetsAt > now && item.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) &&
            item.WindowDurationMinutes is >= 6 * 24 * 60 and <= 8 * 24 * 60)
        .OrderBy(item => Math.Abs(item.WindowDurationMinutes - 7 * 24 * 60))
        .ThenByDescending(item => item.ResetsAt)
        .Select(item => item.ResetsAt!.Value.AddMinutes(-item.WindowDurationMinutes))
        .Where(start => start <= now).Select(start => (DateTimeOffset?)start).FirstOrDefault();

    internal AiLocalUsageSummary Summarize(string mode, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var sunday = monday.AddDays(6);
        mode = SharedUsageResetModes.Normalize(mode);
        DateTimeOffset? start = mode switch
        {
            SharedUsageResetModes.Weekly => new DateTimeOffset(monday.ToDateTime(TimeOnly.MinValue)),
            SharedUsageResetModes.AccountReset => AccountPeriodStart(now),
            _ => null
        };
        string? error = mode == SharedUsageResetModes.AccountReset && start is null
            ? LocalizationService.L("无法确定上次重置时间，请先刷新官方周额度数据", "Last reset time is unknown; refresh official weekly quota data first")
            : null;
        var baseline = _state.ModeBaselines.GetValueOrDefault(mode);
        DateOnly? ambiguousDate = mode == SharedUsageResetModes.AccountReset && start is { } boundary &&
            boundary.LocalDateTime.TimeOfDay != TimeSpan.Zero ? DateOnly.FromDateTime(boundary.LocalDateTime) : null;
        HashSet<string>? sessions = null;
        Dictionary<string, (long Tokens, int Order)>? modelTotals = null;
        // An ambiguous record can appear last. Defer overflow validation until the complete
        // scan confirms that totals are usable, matching the error-first summary behavior.
        Int128 totalTokens = 0;
        if (error is null)
        {
            foreach (var (key, sample) in _state.Samples)
            {
                var cleared = baseline?.GetValueOrDefault(key) ?? 0;
                if (ambiguousDate is { } date && sample.Timestamp is null && sample.Date == date && sample.Tokens > cleared)
                {
                    error = LocalizationService.L("重置当天存在缺少时间戳的记录，无法准确区分重置前后用量", "Some reset-day records have no timestamp; usage before and after the reset cannot be separated accurately");
                    break;
                }
                var occurredAt = sample.OccurredAt;
                if (occurredAt > now || start is { } from && occurredAt < from) continue;
                var tokens = Math.Max(0, sample.Tokens - cleared);
                if (tokens == 0) continue;
                totalTokens += tokens;
                (sessions ??= new()).Add(sample.SessionId);
                if (string.IsNullOrWhiteSpace(sample.Model)) continue;
                modelTotals ??= new();
                modelTotals[sample.Model] = modelTotals.TryGetValue(sample.Model, out var previous)
                    ? (unchecked(previous.Tokens + tokens), previous.Order) : (tokens, modelTotals.Count);
            }
        }
        var usableTotal = error is null ? checked((long)totalTokens) : 0;
        string? mostUsedModel = null;
        if (error is null && modelTotals is not null)
        {
            var mostTokens = -1L;
            var firstOrder = int.MaxValue;
            foreach (var (model, usage) in modelTotals)
            {
                if (usage.Tokens < mostTokens || usage.Tokens == mostTokens && usage.Order >= firstOrder) continue;
                mostTokens = usage.Tokens;
                firstOrder = usage.Order;
                mostUsedModel = model;
            }
        }
        return new AiLocalUsageSummary
        {
            // Keep the existing server's common weekly transport period for group membership.
            // The selected mode controls this device's count independently of that envelope.
            PeriodStart = monday,
            PeriodEnd = sunday,
            CountedFrom = start,
            Error = error,
            TotalTokens = usableTotal,
            SessionCount = error is null ? sessions?.Count ?? 0 : 0,
            MostUsedModel = mostUsedModel
        };
    }
}
