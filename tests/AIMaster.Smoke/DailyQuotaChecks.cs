using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyDailyQuotaPolicy()
    {
        var today = new DateOnly(2026, 10, 11);
        var captured = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(today.ToDateTime(TimeOnly.MinValue)));
        AiManagerSnapshot Snapshot(bool missing = false, bool partialStart = false) => new()
        {
            CapturedAt = captured,
            Limits =
            [
                new AiLimitWindow { LimitId = "codex", IsPrimary = true, UsedPercent = 99, WindowDurationMinutes = 300 },
                new AiLimitWindow { LimitId = "model", IsPrimary = true, UsedPercent = 90, WindowDurationMinutes = 10080, ResetsAt = captured.AddDays(3) },
                new AiLimitWindow { LimitId = "codex", IsPrimary = false, UsedPercent = 40, WindowDurationMinutes = 10080,
                    ResetsAt = new DateTimeOffset(today.AddDays(3).ToDateTime(new TimeOnly(partialStart ? 8 : 0, 0)), captured.Offset) }
            ],
            DailyUsage = Enumerable.Range(0, 5).Where(index => !missing || index != 2)
                .Select(index => new AiDailyUsage { Date = today.AddDays(index - 4), Tokens = index == 4 ? 2000 : 1000 }).ToList(),
            LocalUsage = new AiLocalUsageSummary { TodayTokens = 1, TotalTokens = 1 }
        };
        var estimate = DailyQuotaPolicy.Estimate(Snapshot());
        Check(estimate is { SampleDays: 4 } && Math.Abs(estimate.TodayPercent - 40d / 3) < 0.001 &&
              Math.Abs(estimate.RecommendedPercent!.Value - 8) < 0.001,
            "daily quota uses account history and the Codex weekly window, including a secondary weekly slot");
        Check(DailyQuotaPolicy.Estimate(Snapshot(missing: true)) is null,
            "missing account dates do not fabricate a daily quota estimate");
        Check(DailyQuotaPolicy.Estimate(Snapshot(partialStart: true)) is { SampleDays: 3 },
            "recommendation excludes the incomplete first cycle day and current day");
        Check(DailyQuotaPolicy.Estimate(new AiManagerSnapshot()) is null,
            "no quota or history means no recommendation");
        Check(AiManagerWindow.TryReadDailyQuotaPercent("0.1%", out var low) && low == 0.1 &&
              AiManagerWindow.TryReadDailyQuotaPercent("100", out var high) && high == 100 &&
              !AiManagerWindow.TryReadDailyQuotaPercent("NaN", out _) &&
              !AiManagerWindow.TryReadDailyQuotaPercent("0", out _) &&
              !AiManagerWindow.TryReadDailyQuotaPercent("101", out _),
            "daily percentage validation accepts decimal boundaries and rejects invalid limits");
        var settings = new AiManagerSettings { DailyQuotaWarningEnabled = true, DailyQuotaLimitPercent = 8 };
        var restored = JsonSerializer.Deserialize<AiManagerSettings>(JsonSerializer.Serialize(settings))!;
        Check(restored.DailyQuotaWarningEnabled && restored.DailyQuotaLimitPercent == 8 &&
              !JsonSerializer.Deserialize<AiManagerSettings>("{}")!.DailyQuotaWarningEnabled,
            "daily settings persist while existing users keep the new warning disabled");
        var tracker = new DailyQuotaNotificationTracker();
        Check(!tracker.Evaluate(null, today, settings) && !tracker.Evaluate(7.9, today, settings) &&
              tracker.Evaluate(8, today, settings) && !tracker.Evaluate(9, today, settings) &&
              !tracker.Evaluate(0, today, settings) && !tracker.Evaluate(10, today, settings),
            "daily threshold fires once per day during continuous monitoring despite estimate changes");
        Check(tracker.Evaluate(8, today.AddDays(1), settings),
            "daily notifications rearm on the next local calendar day");
        settings.QuotaNotificationsEnabled = false;
        Check(!tracker.Evaluate(20, today.AddDays(2), settings),
            "the notification master switch silences daily warnings");
        settings.QuotaNotificationsEnabled = true;
        settings.DailyQuotaWarningEnabled = false;
        Check(!tracker.Evaluate(20, today.AddDays(2), settings),
            "daily warning has an independent opt-out");
        var window = new AiManagerWindow();
        Check(window.FindName("DailyQuotaWarningCheck") is CheckBox { Content: TextBlock { Text: "Enable daily account quota warning" } } &&
              window.FindName("UseDailyRecommendationButton") is Button { IsEnabled: false },
            "daily settings are localized and recommendation is disabled without data");
        var render = typeof(AiManagerWindow).GetMethod("RenderDailyQuota", BindingFlags.NonPublic | BindingFlags.Instance)!;
        render.Invoke(window, [Snapshot(), false]);
        var button = (Button)window.FindName("UseDailyRecommendationButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(button.IsEnabled && ((TextBox)window.FindName("DailyQuotaLimitBox")).Text == "8" &&
              ((CheckBox)window.FindName("DailyQuotaWarningCheck")).IsChecked == true,
            "using the recommendation fills the account percentage and enables the pending daily policy");
    }
}
