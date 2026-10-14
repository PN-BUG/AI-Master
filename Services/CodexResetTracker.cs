using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

internal sealed class CodexResetTracker
{
    private readonly string _path;
    private List<string>? _seen;

    public CodexResetTracker(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path)) _seen = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[ResetTracker] Re-establishing baseline: {ex.Message}");
        }
    }

    public IReadOnlyList<CodexResetEvent> Observe(CodexResetSnapshot snapshot, bool notify)
    {
        if (snapshot.IsStale || snapshot.Error is not null) return [];
        var baseline = _seen is null;
        var known = (_seen ?? []).ToHashSet(StringComparer.Ordinal);
        var unseen = snapshot.AlertEvents.Where(item => !known.Contains(item.Key)).ToList();
        if (!baseline && unseen.Count == 0) return [];
        var next = snapshot.AlertEvents.Select(item => item.Key).Concat(_seen ?? [])
            .Distinct(StringComparer.Ordinal).Take(256).ToList();
        // Persist before alerting. Failure leaves the prior baseline intact and emits no notification.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, _path, overwrite: true);
        _seen = next;
        return baseline || !notify ? [] : unseen
            .Where(item => snapshot.CheckedAt - item.AnnouncedAt <= TimeSpan.FromHours(24))
            .OrderByDescending(item => item.AnnouncedAt).ToList();
    }
}

internal sealed class AccountResetTracker
{
    internal sealed record Observation(AiLimitWindow Limit, DateTimeOffset CheckedAt);
    internal sealed class State
    {
        public Dictionary<string, Observation> Previous { get; set; } = new();
        public DateTimeOffset? LastRecoveryAt { get; set; }
    }

    private readonly string? _path;
    private State _state = new();
    public DateTimeOffset? LastRecoveryAt => _state.LastRecoveryAt;

    public AccountResetTracker(string? path = null)
    {
        _path = path;
        try
        {
            if (path is not null && File.Exists(path))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new();
            if (_state.Previous is null) _state = new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[AccountResetTracker] Re-establishing baseline: {ex.Message}");
        }
    }

    public IReadOnlyList<AiLimitWindow> Observe(IEnumerable<AiLimitWindow> limits, DateTimeOffset now)
    {
        var next = new State { Previous = new(_state.Previous), LastRecoveryAt = _state.LastRecoveryAt };
        var recovered = new List<AiLimitWindow>();
        foreach (var current in limits)
        {
            if (current.ResetsAt is null || current.WindowDurationMinutes <= 0 || !double.IsFinite(current.UsedPercent) ||
                current.UsedPercent is < 0 or > 100) continue;
            var start = current.ResetsAt.Value.AddMinutes(-current.WindowDurationMinutes);
            if (start > now) continue;
            // Slot names can change; the account bucket and duration identify the window.
            var key = $"{current.LimitId.ToLowerInvariant()}:{current.WindowDurationMinutes}";
            if (next.Previous.TryGetValue(key, out var old))
            {
                if (now < old.CheckedAt || current.ResetsAt < old.Limit.ResetsAt) continue;
                // The official period crossed our last observation. Low prior usage and
                // consumption accrued before a delayed poll must not suppress a reset alert.
                if (current.ResetsAt > old.Limit.ResetsAt && current.ResetsAt > now &&
                    start <= now && (start >= old.CheckedAt || old.Limit.ResetsAt <= now))
                    recovered.Add(current);
            }
            next.Previous[key] = new(current, now);
        }
        if (next.Previous.Count == 0) return [];
        if (recovered.Count > 0) next.LastRecoveryAt = now;
        // Commit before reporting; a failed write must leave the reset available for retry.
        if (_path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, _path, overwrite: true);
        }
        _state = next;
        return recovered;
    }
}
