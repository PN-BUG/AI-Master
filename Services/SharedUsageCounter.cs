using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

internal sealed record SharedUsageSample(string SessionId, DateOnly Date, string? Model, long Tokens)
{
    public string Key => $"{SessionId}:{Date:yyyy-MM-dd}:{Model}";
}

internal enum SharedUsageCounterAction { None, Clear, Restore }

/// <summary>A device-local ledger. Clearing changes a baseline, never the source logs or other devices.</summary>
internal sealed class SharedUsageCounter
{
    internal sealed class State
    {
        public Dictionary<string, SharedUsageSample> Samples { get; set; } = new();
        public Dictionary<string, long> Baseline { get; set; } = new();
        public Dictionary<string, long>? UndoBaseline { get; set; }
        public Dictionary<string, AiLimitWindow> PreviousLimits { get; set; } = new();
    }

    private readonly string _path;
    private State _state;
    public bool CanRestore => _state.UndoBaseline is not null;

    public SharedUsageCounter(string path)
    {
        _path = path;
        // A damaged state must not silently resurrect cleared usage or lose saved totals.
        _state = File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(path))
                ?? throw new JsonException("Invalid shared usage counter state")
            : new State();
        if (_state.Samples is null || _state.Baseline is null || _state.PreviousLimits is null)
            throw new JsonException("Invalid shared usage counter state");
    }

    public AiLocalUsageSummary Update(IEnumerable<SharedUsageSample> samples, string mode,
        DateTimeOffset now, IEnumerable<AiLimitWindow>? limits = null,
        SharedUsageCounterAction action = SharedUsageCounterAction.None)
    {
        // Work on a copy so a failed atomic save cannot apply a clear only in memory.
        var next = JsonSerializer.Deserialize<State>(JsonSerializer.Serialize(_state))!;
        foreach (var sample in samples.Where(item => item.Tokens > 0))
        {
            // Retain observations across log deletion/truncation and avoid recounting after restart.
            if (!next.Samples.TryGetValue(sample.Key, out var old) || sample.Tokens > old.Tokens)
                next.Samples[sample.Key] = sample;
        }

        var accountRecovered = false;
        foreach (var limit in (limits ?? []).Where(item =>
                     item.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) &&
                     item.WindowDurationMinutes is >= 6 * 24 * 60 and <= 8 * 24 * 60 &&
                     item.ResetsAt is not null && double.IsFinite(item.UsedPercent) &&
                     item.UsedPercent is >= 0 and <= 100))
        {
            var key = $"{limit.LimitId}:{limit.WindowDurationMinutes}:{limit.IsPrimary}";
            if (next.PreviousLimits.TryGetValue(key, out var old) && old.ResetsAt is { } oldReset &&
                limit.ResetsAt > oldReset && limit.ResetsAt > now &&
                (oldReset <= now || old.UsedPercent - limit.UsedPercent >= 20 && limit.UsedPercent <= 10))
                accountRecovered = true;
            next.PreviousLimits[key] = limit;
        }

        mode = SharedUsageResetModes.Normalize(mode);
        if (action == SharedUsageCounterAction.Clear ||
            action == SharedUsageCounterAction.None && mode == SharedUsageResetModes.AccountReset && accountRecovered)
        {
            next.UndoBaseline = new(next.Baseline);
            next.Baseline = next.Samples.ToDictionary(item => item.Key, item => item.Value.Tokens);
        }
        else if (action == SharedUsageCounterAction.Restore && next.UndoBaseline is { } undo)
        {
            next.Baseline = undo;
            next.UndoBaseline = null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, _path, overwrite: true);
        _state = next;
        return Summarize(mode, now);
    }

    internal AiLocalUsageSummary Summarize(string mode, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var sunday = monday.AddDays(6);
        var counted = _state.Samples.Values
            .Where(item => item.Date <= today && (mode != SharedUsageResetModes.Weekly || item.Date >= monday))
            .Select(item => new { Sample = item, Tokens = Math.Max(0, item.Tokens - _state.Baseline.GetValueOrDefault(item.Key)) })
            .Where(item => item.Tokens > 0).ToList();
        return new AiLocalUsageSummary
        {
            // Keep the existing server's common weekly transport period for group membership.
            // The selected mode controls this device's count independently of that envelope.
            PeriodStart = monday,
            PeriodEnd = sunday,
            TotalTokens = counted.Sum(item => item.Tokens),
            SessionCount = counted.Select(item => item.Sample.SessionId).Distinct().Count(),
            MostUsedModel = counted.Where(item => !string.IsNullOrWhiteSpace(item.Sample.Model))
                .GroupBy(item => item.Sample.Model).OrderByDescending(group => group.Sum(item => item.Tokens))
                .Select(group => group.Key).FirstOrDefault()
        };
    }
}
