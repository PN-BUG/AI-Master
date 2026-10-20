using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyDashboardRedesign(string[] args)
    {
        var output = args.SkipWhile(arg => arg != "--output").Skip(1).FirstOrDefault()
                     ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "redesign", "screenshots");
        Directory.CreateDirectory(output);
        var pageButtons = new[] { "QuotaPageButton", "TasksPageButton", "UsagePageButton", "SharedPageButton", "SettingsPageButton" };
        var pageNames = new[] { "quota", "tasks", "usage", "shared", "settings" };
        var now = DateTimeOffset.Parse("2026-10-15T06:13:00Z");
        foreach (var language in new[] { LocalizationService.Chinese, LocalizationService.English })
        foreach (var theme in new[] { ThemeModes.Dark, ThemeModes.Light })
        foreach (var width in new[] { 960, 1280, 1440 })
        {
            LocalizationService.SetLanguage(language);
            var service = new AiManagerService(Path.Combine(output, "isolated", Guid.NewGuid().ToString("N"), "settings.json"));
            service.Settings.Theme = theme;
            var main = new AiManagerWindow(service);
            // Reserve native window chrome when testing the 960 x 650 minimum window.
            var height = width == 960 ? 610 : 900;
            ((Button)main.FindName("ViewUpdateButton")).Visibility = Visibility.Visible;
            ((Button)main.FindName("InstallUpdateButton")).Visibility = Visibility.Visible;
            var root = (FrameworkElement)main.Content;
            typeof(AiManagerWindow).GetMethod("RenderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, new object[] { DocumentationScreenshotCapture.CreateManagerSnapshot(now, LocalizationService.IsEnglish) });
            ((CheckBox)main.FindName("ResetMonitoringCheck")).IsChecked = true;
            ((CheckBox)main.FindName("ResetNotificationsCheck")).IsChecked = true;
            ((CheckBox)main.FindName("SharedUsageEnabledCheck")).IsChecked = true;
            ((TextBox)main.FindName("SharedUsageDeviceNameBox")).Text = LocalizationService.IsEnglish ? "Office PC" : "办公室电脑";
            ((PasswordBox)main.FindName("SharedUsageSyncKeyBox")).Password = "offline-demo-key";
            main.RenderResetSignals(new CodexResetSnapshot(new[]
            {
                new CodexResetEvent("demo-1", "announcement", now,
                    "Day 10 of the ship-or-reset series. The promised global reset was detected at 06:13 UTC. Check your account for official quota recovery.",
                    "https://codex-reset.com/", "发布或重置系列第 10 天。全局重置已在 06:13 UTC 检测到生效，请以账号返回的官方额度为准。"),
                new CodexResetEvent("demo-2", "announcement", now.AddDays(-2),
                    "A global quota reset has rolled out. All changes have propagated.",
                    "https://codex-reset.com/", "全局额度重置已完成，变更已全部生效。")
            }, null, now, now, now, false));

            for (var pageIndex = 0; pageIndex < pageNames.Length; pageIndex++)
            {
                ((RadioButton)main.FindName(pageButtons[pageIndex])).IsChecked = true;
                ArrangeRedesign(root, width, height);
                var scroll = (ScrollViewer)main.FindName("DashboardScrollViewer");
                scroll.ScrollToTop();
                ArrangeRedesign(root, width, height);
                var host = ((Grid)main.FindName("DashboardGrid")).Children.OfType<Grid>().Single(item => item.Visibility == Visibility.Visible);
                var right = host.Children.OfType<StackPanel>().Last();
                var expectedStacked = pageNames[pageIndex] is "quota" or "tasks" or "shared" or "settings";
                Check((host.ColumnDefinitions[2].Width.Value == 0) == expectedStacked,
                    $"{language}/{theme}/{width} {pageNames[pageIndex]} adapts the page columns");
                Check(expectedStacked || Grid.GetColumn(right) == 2,
                    $"{language}/{theme}/{width} {pageNames[pageIndex]} keeps the secondary panel in its column");
                Check(((TextBlock)main.FindName("PageTitleText")).Text.Length > 0 &&
                      ((Border)main.FindName("NavigationRail")).ActualWidth == 216,
                    $"{language}/{theme}/{width} {pageNames[pageIndex]} shows the navigation rail and page heading");
                Check(((TextBlock)main.FindName("PageIndexText")).Visibility == Visibility.Collapsed &&
                      ((TextBlock)main.FindName("PageEyebrowText")).Visibility == Visibility.Collapsed,
                    $"{pageNames[pageIndex]} reserves page space for functional content");
                var controls = (Border)main.FindName("ApplicationControls");
                var settingsButton = (RadioButton)main.FindName("SettingsPageButton");
                Check(settingsButton.TranslatePoint(new Point(0, settingsButton.ActualHeight), root).Y <=
                      controls.TranslatePoint(new Point(), root).Y &&
                      controls.TranslatePoint(new Point(0, controls.ActualHeight), root).Y <= height,
                    $"{language}/{width} navigation and expanded update actions fit without overlapping");
                foreach (var bar in VisualDescendants(root).OfType<ScrollBar>()
                             .Where(bar => bar.Visibility == Visibility.Visible && bar.ActualWidth > 0 && bar.ActualHeight > 0))
                {
                    Check(ReferenceEquals(bar.Style, main.FindResource("ThemedScrollBar")) &&
                          bar.Template.FindName("ScrollRail", bar) is Grid &&
                          (bar.Orientation != Orientation.Vertical || Math.Abs(bar.ActualWidth - 12) < 1),
                        $"{pageNames[pageIndex]} uses the custom scrollbar template");
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"{pageNames[pageIndex]}-{language}-{theme}-{width}.png"));
                encoder.Save(stream);
            }
            // Unsubscribe hidden offline windows before the next language change.
            var handler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), main,
                typeof(AiManagerWindow).GetMethod("LocalizationService_LanguageChanged", BindingFlags.Instance | BindingFlags.NonPublic)!);
            LocalizationService.LanguageChanged -= handler;
            main.Close();
        }
        Console.WriteLine("All five redesigned pages rendered and validated at three widths, in both languages and themes.");
    }

    private static void ArrangeRedesign(FrameworkElement root, int width, int height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }
}
