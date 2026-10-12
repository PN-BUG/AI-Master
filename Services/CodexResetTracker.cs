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
    private readonly Dictionary<string, AiLimitWindow> _previous = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public IReadOnlyList<AiLimitWindow> Observe(IEnumerable<AiLimitWindow> limits, DateTimeOffset now)
    {
        var recovered = new List<AiLimitWindow>();
        foreach (var current in limits)
        {
            if (current.ResetsAt is null || !double.IsFinite(current.UsedPercent) ||
                current.UsedPercent is < 0 or > 100) continue;
            var key = $"{current.LimitId}:{current.WindowDurationMinutes}:{current.IsPrimary}";
            if (_previous.TryGetValue(key, out var old) && old.ResetsAt is { } oldReset &&
                current.ResetsAt > oldReset && current.ResetsAt > now &&
                old.UsedPercent - current.UsedPercent >= 20 && current.UsedPercent <= 10 &&
                _reported.Add($"{key}:{current.ResetsAt.Value.ToUnixTimeSeconds()}")) recovered.Add(current);
            _previous[key] = current;
        }
        return recovered;
    }
}
