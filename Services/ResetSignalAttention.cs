using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

/// <summary>One acknowledgement shared by the dashboard and floating monitor.</summary>
internal sealed class ResetSignalAttention
{
    public static ResetSignalAttention Shared { get; } = new(Path.Combine(
        Path.GetDirectoryName(AppPaths.GetSettingsFile())!, "reset-attention.json"));

    internal sealed record State(CodexResetEvent? Latest, bool Dismissed);
    private readonly string _path;
    private State _state = new(null, false);
    private bool _enabled = true;
    public event EventHandler? Changed;
    public CodexResetEvent? Pending => _enabled && !_state.Dismissed ? _state.Latest : null;

    public ResetSignalAttention(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? _state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"[ResetAttention] Cannot read acknowledgement: {ex.Message}");
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Observe(CodexResetSnapshot snapshot)
    {
        if (snapshot.IsStale || snapshot.Error is not null) return;
        var latest = snapshot.AlertEvents.Where(item => IsRecent(item.AnnouncedAt, snapshot.CheckedAt))
            .OrderByDescending(item => item.AnnouncedAt).ThenByDescending(item => Rank(item.Kind)).FirstOrDefault();
        if (latest is not null) Observe(latest);
    }

    public void ObserveRecovery(DateTimeOffset? recoveredAt, DateTimeOffset now)
    {
        if (recoveredAt is not { } at || !IsRecent(at, now)) return;
        Observe(new CodexResetEvent(at.UtcTicks.ToString(), "recovery", at,
            "Official account data confirms quota recovery.", "",
            "官方账号数据已检测到额度恢复。"));
    }

    private static bool IsRecent(DateTimeOffset at, DateTimeOffset now) =>
        at <= now && now - at <= TimeSpan.FromHours(24);

    private static int Rank(string kind) => kind switch { "recovery" => 2, "announcement" => 1, _ => 0 };

    private void Observe(CodexResetEvent latest)
    {
        if (_state.Latest is { } previous && (latest.Key == previous.Key ||
            latest.AnnouncedAt < previous.AnnouncedAt ||
            latest.AnnouncedAt == previous.AnnouncedAt && Rank(latest.Kind) <= Rank(previous.Kind))) return;
        Save(new(latest, false));
    }

    public void Dismiss()
    {
        if (Pending is null) return;
        Save(_state with { Dismissed = true });
    }

    private void Save(State next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, _path, overwrite: true);
        _state = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Title => Pending?.Kind switch
    {
        "recovery" => LocalizationService.L("账号额度已恢复", "Account quota recovered"),
        "announcement" => LocalizationService.L("收到重置公告", "Reset announced"),
        _ => LocalizationService.L("最新重置预告", "New reset preview")
    };
    public string DismissText => LocalizationService.L("已知晓 · 点击隐藏", "Dismiss reminder");
    public string TimeText => Pending is { } item ? item.LocalResetTimeCompactText is { Length: > 0 } time
        ? time : LocalizationService.L("发布于 ", "Posted ") + item.TimeText : "";
    public string Detail => Pending is { } item
        ? (item.LocalResetTimeText.Length > 0 ? item.LocalResetTimeText + "\n" : "") +
          $"{LocalizationService.L("发布于", "Posted")} {item.TimeText} · {item.SummaryText}" + (item.Kind == "recovery" ? "" :
            LocalizationService.L("\n社区转述，到账以账号数据为准。", "\nCommunity report; check account quota."))
        : "";
}
