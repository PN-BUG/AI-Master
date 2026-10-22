using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

/// <summary>Records requests and Notification Center retention separately from reset observations.</summary>
internal sealed class ResetNotificationHistory
{
    private sealed record State(DateTimeOffset? LastSentAt = null, DateTimeOffset? LastRequestedAt = null,
        string? LastRequestedEventKey = null, DateTimeOffset? LastShownAt = null, string? LastShownEventKey = null,
        DateTimeOffset? LastRetainedAt = null, string? LastRetainedEventKey = null, string? LastRetainedChannel = null);
    private readonly string _path;
    private readonly TimeZoneInfo _timeZone;
    private readonly Func<DateTimeOffset> _clock;
    private State _state = new();
    public DateTimeOffset? LastRequestedAt => _state.LastRequestedAt ?? _state.LastSentAt;
    public DateTimeOffset? LastShownAt => _state.LastShownAt;
    public DateTimeOffset? LastRetainedAt => _state.LastRetainedAt;

    public ResetNotificationHistory(string path, TimeZoneInfo? timeZone = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _clock = clock ?? (() => DateTimeOffset.Now);
        try
        {
            if (File.Exists(path))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[ResetNotifications] Cannot read sent history: {ex.Message}");
        }
    }

    public bool WasSentToday(DateTimeOffset now) =>
        _state.LastRetainedChannel == "registered-center" && _state.LastRetainedAt is { } retained && IsToday(retained, now);

    public bool CanCatchUp(DateTimeOffset? resetAt, DateTimeOffset now) =>
        resetAt is { } reset && IsToday(reset, now) && !WasSentToday(now);

    public CodexResetEvent? FindCatchUp(CodexResetSnapshot snapshot, DateTimeOffset now) =>
        snapshot.IsStale || snapshot.Error is not null ? null : snapshot.AlertEvents
            .Where(item => item.Kind is "signal" or "announcement" && CanCatchUp(item.AnnouncedAt, now))
            .OrderByDescending(item => item.AnnouncedAt).FirstOrDefault();

    private bool IsToday(DateTimeOffset at, DateTimeOffset now) => at <= now &&
        TimeZoneInfo.ConvertTime(at, _timeZone).Date == TimeZoneInfo.ConvertTime(now, _timeZone).Date;

    public bool TrySend(Func<Action, bool> send, DateTimeOffset now, string eventKey,
        Action<Exception>? onConfirmationError = null)
    {
        var confirmed = false;
        if (!send(() =>
            {
                if (confirmed) return;
                confirmed = true;
                // Old balloon display receipts do not prove Notification Center retention.
                _state = _state with { LastRetainedAt = _clock(), LastRetainedEventKey = eventKey, LastRetainedChannel = "registered-center" };
                try { Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"[ResetNotifications] Cannot save Notification Center receipt: {ex.Message}");
                    onConfirmationError?.Invoke(ex);
                }
            })) return false;
        _state = _state with { LastRequestedAt = now, LastRequestedEventKey = eventKey };
        Save();
        return true;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, _path, overwrite: true);
    }
}
