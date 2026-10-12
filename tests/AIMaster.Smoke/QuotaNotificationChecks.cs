using System;
using System.Linq;
using System.Text.Json;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyQuotaNotifications()
    {
        VerifyDailyQuotaPolicy();
        var settings = new AiManagerSettings { AutoPause = false };
        Check(JsonSerializer.Deserialize<AiManagerSettings>("{}")!.QuotaNotificationsEnabled,
            "existing settings enable quota notifications by default");
        settings.QuotaNotificationsEnabled = false;
        var json = JsonSerializer.Serialize(settings);
        Check(json.Contains("\"quotaNotificationsEnabled\":false", StringComparison.Ordinal) &&
              !JsonSerializer.Deserialize<AiManagerSettings>(json)!.QuotaNotificationsEnabled,
            "notification opt-out survives settings serialization");

        var tracker = new QuotaNotificationTracker();
        AiLimitWindow Limit(double used, bool primary = true) => new()
        {
            LimitId = primary ? "weekly" : "model", Name = primary ? "Codex" : "Model quota",
            UsedPercent = used, IsPrimary = primary, WindowDurationMinutes = primary ? 10080 : 300
        };
        Check(tracker.Evaluate([Limit(95)], settings) is null,
            "disabled notifications stay silent above both limits");
        settings.QuotaNotificationsEnabled = true;
        Check(tracker.Evaluate([Limit(69.9)], settings) is null,
            "usage below the warning boundary stays silent");
        var secondary = Limit(70, primary: false);
        Check(tracker.Evaluate([Limit(30), secondary], settings) is
              { IsPauseLimit: false, Limit: var selected } && ReferenceEquals(selected, secondary),
            "notifications use the highest window and work without automatic pause");
        Check(tracker.Evaluate([Limit(85)], settings) is null &&
              tracker.Evaluate([], settings) is null &&
              tracker.Evaluate([Limit(double.NaN)], settings) is null &&
              tracker.Evaluate([Limit(85)], settings) is null,
            "repeated refreshes and missing or invalid data do not repeat warnings");
        Check(tracker.Evaluate([Limit(90)], settings) is { IsPauseLimit: true },
            "reaching the pause boundary upgrades the notification once");
        Check(tracker.Evaluate([Limit(99)], settings) is null &&
              tracker.Evaluate([Limit(80)], settings) is null &&
              tracker.Evaluate([Limit(95)], settings) is null,
            "an ongoing breach does not repeat pause or warning notifications");
        Check(tracker.Evaluate([Limit(50)], settings) is null &&
              tracker.Evaluate([Limit(70)], settings) is { IsPauseLimit: false },
            "recovery below the warning line rearms notifications");
        settings.QuotaNotificationsEnabled = false;
        Check(tracker.Evaluate([Limit(95)], settings) is null,
            "turning notifications off silences an active breach");
        settings.QuotaNotificationsEnabled = true;
        Check(tracker.Evaluate([Limit(95)], settings) is { IsPauseLimit: true } &&
              tracker.Evaluate([Limit(95)], settings) is null,
            "reenabling during a breach sends only the current severity once");

        var window = new AiManagerWindow();
        var checkbox = window.FindName("QuotaNotificationsCheck") as CheckBox;
        Check(checkbox?.Content is TextBlock { Text: "Quota warning notifications" },
            "guard policy exposes the notification switch in English");
        LocalizationService.Initialize(LocalizationService.Chinese);
        LocalizationService.Apply(window);
        Check(checkbox?.Content is TextBlock { Text: "额度预警通知栏提示" },
            "notification switch supports Chinese");
        LocalizationService.Initialize(LocalizationService.English);
    }
}
