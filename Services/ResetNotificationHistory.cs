using System.Text.Json;

namespace AIMaster.Services;

/// <summary>Records notification requests separately from reset observations.</summary>
internal sealed class ResetNotificationHistory
{
    private sealed record State(DateTimeOffset? LastSentAt);
    private readonly string _path;
    private readonly TimeZoneInfo _timeZone;
    private State _state = new(null);

    public ResetNotificationHistory(string path, TimeZoneInfo? timeZone = null)
    {
        _path = path;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        try
        {
            if (File.Exists(path))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[ResetNotifications] Cannot read sent history: {ex.Message}");
        }
    }

    public bool WasSentToday(DateTimeOffset now) =>
        _state.LastSentAt is { } sent && IsToday(sent, now);

    public bool CanCatchUp(DateTimeOffset? resetAt, DateTimeOffset now) =>
        resetAt is { } reset && IsToday(reset, now) && !WasSentToday(now);

    private bool IsToday(DateTimeOffset at, DateTimeOffset now) => at <= now &&
        TimeZoneInfo.ConvertTime(at, _timeZone).Date == TimeZoneInfo.ConvertTime(now, _timeZone).Date;

    public bool TrySend(Func<bool> send, DateTimeOffset now)
    {
        // A disabled system notification switch or unavailable tray must not consume catch-up.
        if (!send()) return false;
        // Retain the successful request in memory even if saving fails, avoiding a poll-time burst.
        _state = new(now);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, _path, overwrite: true);
        return true;
    }
}
