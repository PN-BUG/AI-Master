using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    /// <summary>Covers the configurable floating-monitor header: style normalization, settings
    /// persistence and the text formatters shared by every style.</summary>
    private static void VerifyFloatingHeaderStyles()
    {
        LocalizationService.SetLanguage(LocalizationService.English);

        Check(FloatingHeaderStyles.Normalize(null) == FloatingHeaderStyles.QuotaReset &&
              FloatingHeaderStyles.Normalize("nonsense") == FloatingHeaderStyles.QuotaReset,
            "unknown header styles fall back to the quota countdown");
        foreach (var style in new[]
                 {
                     FloatingHeaderStyles.Brand, FloatingHeaderStyles.QuotaReset,
                     FloatingHeaderStyles.TodayUsage, FloatingHeaderStyles.QuotaAlert,
                     FloatingHeaderStyles.LatestEvent, FloatingHeaderStyles.UsageTrend,
                     FloatingHeaderStyles.Minimal
                 })
        {
            Check(FloatingHeaderStyles.Normalize(style) == style, $"header style {style} round-trips");
        }
        Check(FloatingHeaderStyles.NeedsUsage(FloatingHeaderStyles.TodayUsage) &&
              FloatingHeaderStyles.NeedsUsage(FloatingHeaderStyles.UsageTrend) &&
              !FloatingHeaderStyles.NeedsUsage(FloatingHeaderStyles.QuotaReset),
            "only usage header styles request usage data");
        Check(new AiManagerSettings().FloatingHeaderStyle == FloatingHeaderStyles.QuotaReset,
            "new installations start on the quota countdown header");
        Check(JsonSerializer.Deserialize<AiManagerSettings>(
                  "{\"floatingHeaderStyle\":\"usageTrend\"}")!.FloatingHeaderStyle ==
              FloatingHeaderStyles.UsageTrend,
            "floating header style persists through settings JSON");

        Check(AiFloatingWindow.FormatCountdown(TimeSpan.FromMinutes(90)) == "resets in 1h 30m",
            "countdown formats hours and minutes");
        Check(AiFloatingWindow.FormatCountdown(TimeSpan.FromDays(3) + TimeSpan.FromHours(2)) ==
              "resets in 3d 2h", "countdown formats days and hours");
        Check(AiFloatingWindow.FormatResetCountdown(TimeSpan.FromMinutes(90)) == "resets in 1h" &&
              AiFloatingWindow.FormatResetCountdown(TimeSpan.FromMinutes(45)) == "resets in 45m" &&
              AiFloatingWindow.FormatResetCountdown(TimeSpan.FromDays(2) + TimeSpan.FromHours(3)) ==
              "resets in 2d3h",
            "header countdown uses one compact unit");
        Check(AiFloatingWindow.FormatFreshness(TimeSpan.FromSeconds(2)) == "now",
            "freshness shows now for very recent data");
        Check(AiFloatingWindow.FormatFreshness(TimeSpan.FromSeconds(42)) == "42s ago",
            "freshness shows seconds");
        Check(AiFloatingWindow.FormatTokenCount(128_000) == "128K" &&
              AiFloatingWindow.FormatTokenCount(1_200_000) == "1.2M",
            "token counts are compacted for the header");

        var fiveHour = new AiLimitWindow { UsedPercent = 0, ResetsAt = DateTimeOffset.Now.AddHours(2) };
        var weekly = new AiLimitWindow { UsedPercent = 99, ResetsAt = DateTimeOffset.Now.AddDays(2) };
        var quotaSnapshot = new AiFloatingSnapshot
        {
            Provider = AiProviderModes.Codex,
            FiveHourQuota = fiveHour,
            WeeklyQuota = weekly,
            CodexFiveHourQuota = fiveHour,
            CodexWeeklyQuota = weekly
        };
        var countdown = AiFloatingWindow.FormatQuotaCountdown(quotaSnapshot, DateTimeOffset.Now);
        Check(countdown.StartsWith("Week 1%", StringComparison.Ordinal) &&
              countdown.Contains("resets in", StringComparison.Ordinal),
            "quota countdown follows the tightest quota window");
        var quotaAlert = AiFloatingWindow.FormatQuotaAlert(quotaSnapshot, 70, 0);
        Check(quotaAlert is { Text: "Week 1% left" } && quotaAlert.Value.ColorKey == "WarningText",
            "quota alert names the exhausted weekly window");
        Check(AiFloatingWindow.FormatQuotaAlert(quotaSnapshot, 100, 0) is null,
            "quota alert stays quiet below the configured threshold");

        var balanceSnapshot = new AiFloatingSnapshot
        {
            Provider = AiProviderModes.Codex,
            DeepSeekBalance = new AiDeepSeekBalance { Total = 0.06 }
        };
        var balanceAlert = AiFloatingWindow.FormatQuotaAlert(balanceSnapshot, 100, 1);
        Check(balanceAlert is { Text: "Balance ¥0.06" } && balanceAlert.Value.ColorKey == "DangerText",
            "balance alert uses the configured balance warning threshold");

        var tasks = new List<AiThreadSummary>
        {
            new() { Id = "running", Status = "inProgress", UpdatedAt = DateTimeOffset.Now.AddMinutes(-2) },
            new() { Id = "waiting", Status = "waitingOnApproval", UpdatedAt = DateTimeOffset.Now.AddMinutes(-1) }
        };
        Check(AiFloatingWindow.FormatTaskOverview(tasks) == "1 waiting · 1 running",
            "task overview counts waiting and running tasks");
        Check(AiFloatingWindow.FormatTaskOverview(new List<AiThreadSummary>()) == "No tasks",
            "empty task list has a stable overview");
        Check(AiFloatingWindow.FormatLatestEvent(tasks, null, null, DateTimeOffset.Now)
                  .Contains("Awaiting approval", StringComparison.Ordinal),
            "latest event prefers the newest task activity");

        var localUsage = new AiLocalUsageSummary
        {
            TodayTokens = 128_000,
            TotalTokens = 1_200_000,
            DailyUsage = Enumerable.Range(0, 7).Select(offset => new AiDailyUsage
            {
                Date = DateOnly.FromDateTime(DateTime.Now).AddDays(offset - 6),
                Tokens = (offset + 1) * 1000
            }).ToList()
        };
        var usageSnapshot = new AiFloatingSnapshot
        {
            Provider = AiProviderModes.Codex,
            LocalCodexUsage = localUsage,
            LocalUsageCapturedAt = DateTimeOffset.Now
        };
        Check(AiFloatingWindow.FormatTodayUsage(usageSnapshot).Text == "Today 128K · Week 1.2M",
            "today usage compacts local token counts");
        var trend = AiFloatingWindow.HeaderDailyTokens(usageSnapshot);
        Check(trend.Count == 7 && trend[^1].Date == DateOnly.FromDateTime(DateTime.Now),
            "usage trend ends with today");
    }
}
