using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyDashboardPages(AiManagerWindow main)
    {
        var service = (AiManagerService)typeof(AiManagerWindow)
            .GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        // Exercise pre-page layout preferences, including settings and shared cards in the right column.
        service.Settings.DashboardCardLayout = ["right:quota", "right:settings", "right:sharedUsage"];
        typeof(AiManagerWindow).GetMethod("ApplyDashboardLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(main, null);

        var host = (Grid)main.FindName("DashboardGrid");
        Check(host.Children.OfType<Grid>().Count() == 5 &&
              host.Children.OfType<Grid>().Single(page => page.Visibility == Visibility.Visible).Tag is "quota",
            "the main window starts on quota overview with exactly one of five pages visible");
        var expected = new[]
        {
            ("QuotaPageButton", "quota", new[] { "QuotaCard", "LimitsCard", "ResetSignalsCard" }),
            ("TasksPageButton", "tasks", new[] { "ThreadsCard", "GuardCard" }),
            ("UsagePageButton", "usage", new[] { "LocalUsageCard", "DailyUsageCard", "ForecastCard" }),
            ("SharedPageButton", "shared", new[] { "SharedUsageCard" }),
            ("SettingsPageButton", "settings", new[] { "SettingsCard", "ApplicationSettingsCard" })
        };
        var policy = (TextBox)main.FindName("WarningPercentBox");
        policy.Text = "73";
        foreach (var (buttonName, pageId, cardNames) in expected)
        {
            ((RadioButton)main.FindName(buttonName)).IsChecked = true;
            var active = host.Children.OfType<Grid>().Single(page => page.Visibility == Visibility.Visible);
            Check(Equals(active.Tag, pageId) && cardNames.All(name =>
                      ReferenceEquals(AiManagerWindow.FindAncestor<Grid>(((Border)main.FindName(name)).Parent), active)),
                $"{pageId} navigation exposes the correct cards in one dedicated page");
            Check(main.CaptureDashboardLayout().Count == 10 &&
                  main.CaptureDashboardLayout().Select(entry => entry.Split(':')[1]).Distinct().Count() == 10,
                $"saving a layout from {pageId} retains cards on hidden pages");
            var description = (TextBlock)main.FindName("PageDescriptionText");
            Check(!string.IsNullOrWhiteSpace(description.Text) && !description.Text.Any(c => c >= '\u4e00' && c <= '\u9fff'),
                $"{pageId} page description is localized in English");
        }
        Check(policy.Text == "73", "unsaved form edits survive page switches");
        var shared = (Border)main.FindName("SharedUsageCard");
        var sharedHost = AiManagerWindow.FindAncestor<Grid>(shared.Parent)!;
        Check(sharedHost.ColumnDefinitions[2].Width.Value == 0 &&
              ReferenceEquals(shared.Parent, sharedHost.Children[0]),
            "legacy shared-layout preferences migrate into a full-width page");
        Check(main.CaptureDashboardLayout().Contains("right:quota") &&
              main.CaptureDashboardLayout().Contains("left:settings"),
            "legacy ordering is retained while settings reserve a separate column for app controls");
        LocalizationService.SetLanguage(LocalizationService.Chinese);
        Check(((RadioButton)main.FindName("TasksPageButton")).Content is "任务监控" &&
              ((TextBlock)main.FindName("PageDescriptionText")).Text == "调整保护策略、浮窗字号、语言和主题，管理版本更新。",
            "page navigation and the active description switch to Chinese");
        LocalizationService.SetLanguage(LocalizationService.English);
        ((RadioButton)main.FindName("QuotaPageButton")).IsChecked = true;
    }
}
