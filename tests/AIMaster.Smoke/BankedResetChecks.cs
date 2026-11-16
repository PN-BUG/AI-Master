using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyBankedResetNotices()
    {
        var now = DateTimeOffset.Parse("2026-11-16T04:00:00Z");
        var at = DateTimeOffset.Parse("2026-11-12T21:59:39Z");
        var source = "https://x.com/thsottiaux/status/2120999993079451919";
        object Row(string id, string origin, string? url, string state, DateTimeOffset time) => new
        {
            id, group = "credits", reset_kind = "banked", source = origin, url,
            banked_state = state, announcement_state = "none", announced_at = time,
            summary = "Banked reset activated", localized_summary = "重置卡已开始发放"
        };
        using var feed = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            fetched_at = now, events = new[] {
                Row("2120999993079451919", "live", source, "unknown", at),
                Row("2121007235465577607", "observed", null, "available", at.AddMinutes(29)),
                Row("nov1", "observed", null, "available", at.AddDays(-11)),
                Row("unsafe", "live", "https://evil.example/", "unknown", at),
                Row("missing-url", "live", null, "unknown", at)
            }
        }));
        using var forecast = JsonDocument.Parse(JsonSerializer.Serialize(new { updated_at = now }));
        var snapshot = CodexResetClient.Parse(feed.RootElement, forecast.RootElement, now);
        Check(snapshot.Events.Count == 3 && snapshot.Events[0].Kind == "banked-observed" &&
              snapshot.Events[0].Url == "https://codex-reset.com/" && snapshot.Events[1].Kind == "banked",
            "actual banked contracts parse without announced state; observed grants need no tweet URL; unsafe sources rejected");
        Check(snapshot.Events[1].ChineseSummary == "重置卡已开始发放" &&
              snapshot.Events[1].IsRecentAlert(now) && !snapshot.Events[2].IsRecentAlert(now),
            "delayed banked announcements retain translations and a bounded seven-day reminder window");
        var directory = Path.Combine(Path.GetTempPath(), "aimaster-banked-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "tracker.json");
            var tracker = new CodexResetTracker(path);
            tracker.Observe(snapshot with { Events = [] }, true);
            Check(tracker.Observe(snapshot with { IsStale = true }, true).Count == 0 &&
                  tracker.Observe(snapshot, true).Count == 2 &&
                  new CodexResetTracker(path).Observe(snapshot, true).Count == 0,
                "banked grants notify once after upgrade; stale responses do not consume notices; old grants stay in history");
            var attention = new ResetSignalAttention(Path.Combine(directory, "attention.json"));
            attention.Observe(snapshot);
            Check(attention.Pending?.Kind == "banked-observed" && attention.Title == attention.Pending.KindText,
                "dashboard and floating reminder identify monitoring-account banked grants without claiming account recovery");
            attention.Dismiss();
            attention.Observe(snapshot);
            Check(attention.Pending is null, "banked reminder dismissal survives repeated polling");
            var history = new ResetNotificationHistory(Path.Combine(directory, "notification.json"));
            Check(history.FindCatchUp(snapshot with { Events = [snapshot.Events[0] with { AnnouncedAt = now }] }, now)?.IsBanked == true,
                "today's banked grants participate in missed-notification replay");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
