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
        public int FormatVersion { get; set; } = 2;
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
            var json = File.ReadAllText(path);
            _state = JsonSerializer.Deserialize<State>(json) ?? throw new JsonException("Invalid shared usage counter state");
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(nameof(State.FormatVersion), out _))
            {
                _state.FormatVersion = 1;
                _state.LegacyObservedThrough = File.GetLastWriteTimeUtc(path);
            }
        }
        else _state = new State();
        if (_state.Samples is null || _state.Baseline is null || _state.PreviousLimits is null ||
            _state.ModeBaselines is null || _state.ModeUndoBaselines is null)
            throw new JsonException("Invalid shared usage counter state");
    }

    public AiLocalUsageSummary Update(IEnumerable<SharedUsageSample> samples, string mode,
        DateTimeOffset now, IEnumerable<AiLimitWindow>? limits = null,
        SharedUsageCounterAction action = SharedUsageCounterAction.None)
    {
        // Work on a copy so a failed atomic save cannot apply a clear only in memory.
        var next = JsonSerializer.Deserialize<State>(JsonSerializer.Serialize(_state))!;
        mode = SharedUsageResetModes.Normalize(mode);
        var incoming = samples.Where(item => item.Tokens > 0).GroupBy(item => item.Key)
            .Select(group => group.MaxBy(item => item.Tokens)!).ToList();
        if (next.FormatVersion < 2) MigrateDailyState(next, incoming, _legacyMode);
        ReconcileDailyResiduals(next, incoming);
        foreach (var sample in incoming)
        {
            // Retain observations across log deletion/truncation and avoid recounting after restart.
            if (!next.Samples.TryGetValue(sample.Key, out var old) || sample.Tokens > old.Tokens)
                next.Samples[sample.Key] = sample;
        }

        foreach (var limit in (limits ?? []).Where(item =>
                     item.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) &&
                     item.WindowDurationMinutes is >= 6 * 24 * 60 and <= 8 * 24 * 60 &&
                     item.ResetsAt is not null && double.IsFinite(item.UsedPercent) &&
                     item.UsedPercent is >= 0 and <= 100))
        {
            var key = $"{limit.LimitId}:{limit.WindowDurationMinutes}:{limit.IsPrimary}";
            next.PreviousLimits[key] = limit;
        }

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
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, _path, overwrite: true);
        _state = next;
        return Summarize(mode, now);
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
        var baseline = _state.ModeBaselines.GetValueOrDefault(mode) ?? new();
        if (mode == SharedUsageResetModes.AccountReset && start is { } boundary && boundary.LocalDateTime.TimeOfDay != TimeSpan.Zero &&
            _state.Samples.Values.Any(item => item.Timestamp is null && item.Date == DateOnly.FromDateTime(boundary.LocalDateTime) &&
                item.Tokens > baseline.GetValueOrDefault(item.Key)))
            error = LocalizationService.L("重置当天存在缺少时间戳的记录，无法准确区分重置前后用量", "Some reset-day records have no timestamp; usage before and after the reset cannot be separated accurately");
        var counted = _state.Samples.Values
            .Where(item => item.OccurredAt <= now && (start is null || item.OccurredAt >= start))
            .Select(item => new { Sample = item, Tokens = Math.Max(0, item.Tokens - baseline.GetValueOrDefault(item.Key)) })
            .Where(item => item.Tokens > 0).ToList();
        return new AiLocalUsageSummary
        {
            // Keep the existing server's common weekly transport period for group membership.
            // The selected mode controls this device's count independently of that envelope.
            PeriodStart = monday,
            PeriodEnd = sunday,
            CountedFrom = start,
            Error = error,
            TotalTokens = error is null ? counted.Sum(item => item.Tokens) : 0,
            SessionCount = error is null ? counted.Select(item => item.Sample.SessionId).Distinct().Count() : 0,
            MostUsedModel = (error is null ? counted : []).Where(item => !string.IsNullOrWhiteSpace(item.Sample.Model))
                .GroupBy(item => item.Sample.Model).OrderByDescending(group => group.Sum(item => item.Tokens))
                .Select(group => group.Key).FirstOrDefault()
        };
    }
}
