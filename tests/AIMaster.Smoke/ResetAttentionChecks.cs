using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyResetAttention(string[] args)
    {
        VerifyResetLocalTimes();
        VerifyNewestResetPreview();
        if (args.Contains("--reset-preview-live", StringComparer.OrdinalIgnoreCase))
        {
            using var client = new CodexResetClient();
            var live = client.ReadAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Live response: checked={live.CheckedAt:O}; fetched={live.FetchedAt:O}; stale={live.IsStale}; error={live.Error}");
            Check(!live.IsStale && live.Error is null, "live reset preview response is fresh");
            Console.WriteLine($"Live preview: {live.Signal?.Id} · {live.Signal?.Summary} · {live.Signal?.LocalResetTimeText}");
        }
        var output = args.SkipWhile(arg => arg != "--output").Skip(1).FirstOrDefault()
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "reset-attention", "screenshots");
        Directory.CreateDirectory(output);
        var now = DateTimeOffset.Now;
        var preview = new CodexResetEvent("preview", "signal", now.AddMinutes(-3),
            "Lands tomorrow before 10am PST.", "https://codex-reset.com/", "明天上午 10 点 PST 前到达。");
        var announced = preview with { Kind = "announcement", AnnouncedAt = now.AddMinutes(-2) };
        CodexResetSnapshot Snapshot(CodexResetEvent item) => new(
            item.Kind == "signal" ? [] : [item], item.Kind == "signal" ? item : null,
            null, now, now, false);
        var statePath = Path.Combine(output, "isolated", Guid.NewGuid().ToString("N"), "attention.json");
        var attention = new ResetSignalAttention(statePath);
        attention.Observe(Snapshot(preview) with { IsStale = true });
        attention.Observe(Snapshot(preview with { AnnouncedAt = now.AddMinutes(1) }));
        attention.Observe(Snapshot(preview with { AnnouncedAt = now.AddDays(-2) }));
        Check(attention.Pending is null, "stale, future and historical events do not create reminders");
        attention.Observe(Snapshot(preview));
        attention.Dismiss();
        attention = new ResetSignalAttention(statePath);
        attention.Observe(Snapshot(preview));
        Check(attention.Pending is null, "acknowledged preview stays hidden after restart and refresh");
        attention.Observe(Snapshot(announced));
        Check(attention.Pending?.Kind == "announcement", "announcement resurfaces after preview acknowledgement");
        attention.ObserveRecovery(now.AddMinutes(-1), now);
        attention.Dismiss();
        attention.Observe(Snapshot(announced));
        Check(new ResetSignalAttention(statePath).Pending is null,
            "acknowledged recovery cannot be replaced by an older community event");
        attention.SetEnabled(false);
        attention.Observe(Snapshot(preview with { Id = "new", AnnouncedAt = now }));
        Check(attention.Pending is null, "monitoring off hides the reminder");
        attention.SetEnabled(true);
        Check(attention.Pending?.Id == "new", "monitoring on restores the unacknowledged reminder");

        foreach (var language in new[] { LocalizationService.Chinese, LocalizationService.English })
        foreach (var theme in new[] { ThemeModes.Dark, ThemeModes.Light })
        {
            LocalizationService.SetLanguage(language);
            var directory = Path.Combine(output, "isolated", Guid.NewGuid().ToString("N"));
            attention = new ResetSignalAttention(Path.Combine(directory, "attention.json"));
            var service = new AiManagerService(Path.Combine(directory, "settings.json"));
            service.Settings.Theme = theme;
            var main = new AiManagerWindow(service, attention);
            var floatingService = new AiManagerService(Path.Combine(directory, "floating-settings.json"));
            floatingService.Settings.Theme = theme;
            var floating = new AiFloatingWindow(floatingService, attention);
            typeof(AiFloatingWindow).GetMethod("RenderSnapshot", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(floating,
                [new AiFloatingSnapshot { TaskName = "Review reset signals", TaskStatus = "inProgress",
                    ModelName = "GPT-5", ReasoningEffort = "high", RemainingPercent = 63, CapturedAt = now }]);
            var sideButton = (Button)main.FindName("ResetAttentionButton");
            var floatButton = (Button)floating.FindName("ResetAttentionButton");
            main.RenderResetSignals(Snapshot(preview));
            Check(sideButton.Visibility == Visibility.Visible && floatButton.Visibility == Visibility.Visible,
                "a new signal is visible in both windows");
            Check(((TextBlock)main.FindName("ResetAttentionDetail")).Text == attention.TimeText &&
                  ((TextBlock)floating.FindName("ResetAttentionTime")).Text == attention.TimeText &&
                  attention.Detail.Contains("UTC"), "both reminder surfaces show the converted local deadline");
            CaptureState("preview");
            sideButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(sideButton.Visibility == Visibility.Collapsed && floatButton.Visibility == Visibility.Collapsed,
                "sidebar click hides both reminders");
            main.RenderResetSignals(Snapshot(preview));
            Check(sideButton.Visibility == Visibility.Collapsed, "polling does not restore an acknowledged signal");
            main.RenderResetSignals(Snapshot(announced));
            CaptureState("announced");
            floatButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(sideButton.Visibility == Visibility.Collapsed && floatButton.Visibility == Visibility.Collapsed,
                "floating click also hides both reminders");
            attention.ObserveRecovery(now.AddSeconds(-20), now);
            Check(((TextBlock)main.FindName("ResetAttentionTitle")).Text.Contains(
                LocalizationService.IsEnglish ? "recovered" : "已恢复"), "official recovery has its own title");

            foreach (var width in new[] { 960, 1280 })
            {
                ((RadioButton)main.FindName("TasksPageButton")).IsChecked = true;
                ((Button)main.FindName("ViewUpdateButton")).Visibility = Visibility.Visible;
                ((Button)main.FindName("InstallUpdateButton")).Visibility = Visibility.Visible;
                var root = (FrameworkElement)main.Content;
                ArrangeRedesign(root, width, 610);
                var controls = (Border)main.FindName("ApplicationControls");
                Check(sideButton.ActualHeight > 70 && sideButton.TranslatePoint(new Point(0, sideButton.ActualHeight), root).Y <=
                    controls.TranslatePoint(new Point(), root).Y, "reminder fits above sidebar controls at minimum height");
                Capture(root, width, 610, $"sidebar-{language}-{theme}-{width}");
            }
            foreach (var scale in new[] { 1d, 1.4d })
            {
                typeof(AiFloatingWindow).GetMethod("ApplyFontScale", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.Invoke(floating, [scale]);
                foreach (var width in new[] { 180, 260 })
                {
                    var root = (FrameworkElement)floating.Content;
                    var height = (int)Math.Ceiling(floating.MinHeight);
                    ArrangeRedesign(root, width, height);
                    Check(floatButton.ActualHeight >= 44 * scale - 1 && floatButton.ActualWidth > 120,
                        "floating reminder fits at narrow width and enlarged font");
                    foreach (var text in VisualDescendants(floatButton).OfType<TextBlock>())
                        Check(text.TranslatePoint(new Point(0, text.ActualHeight), floatButton).Y <= floatButton.ActualHeight,
                            "floating reminder text stays inside its button");
                    Capture(root, width, height, $"floating-{language}-{theme}-{width}-{scale:0.0}");
                }
            }
            attention.Dismiss();
            Check(floating.MinHeight == 98 && floatButton.Visibility == Visibility.Collapsed,
                "dismissing restores the compact floating height");
            CaptureState("dismissed");
            // These windows are rendered offline; their Loaded/network handlers never run.
            main.Close();
            floating.Close();

            void CaptureState(string state)
            {
                var root = (FrameworkElement)main.Content;
                ArrangeRedesign(root, 960, 610);
                Capture(root, 960, 610, $"{state}-sidebar-{language}-{theme}");
                root = (FrameworkElement)floating.Content;
                var height = (int)Math.Ceiling(floating.MinHeight);
                ArrangeRedesign(root, 180, height);
                Capture(root, 180, height, $"{state}-floating-{language}-{theme}");
                if (state != "preview") return;
                foreach (var scale in new[] { 1d, 1.4d })
                {
                    typeof(AiFloatingWindow).GetMethod("ApplyFontScale", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic)!.Invoke(floating, [scale]);
                    height = (int)Math.Ceiling(floating.MinHeight);
                    ArrangeRedesign(root, 180, height);
                    foreach (var text in VisualDescendants(floatButton).OfType<TextBlock>())
                        Check(text.TranslatePoint(new Point(0, text.ActualHeight), floatButton).Y <= floatButton.ActualHeight,
                            "local deadline and dismiss action fit at narrow width and enlarged font");
                    Capture(root, 180, height, $"{state}-floating-{language}-{theme}-{scale:0.0}");
                }
            }
        }
        Console.WriteLine("Reset attention persistence, shared clicks, and rendered layout checks passed.");

        void Capture(FrameworkElement root, int width, int height, string name)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(stream);
        }
    }

    private static void VerifyResetLocalTimes()
    {
        var posted = DateTimeOffset.Parse("2026-10-22T06:55:54Z");
        var item = new CodexResetEvent("time", "signal", posted,
            "Lands tomorrow before 10am PST.", "https://codex-reset.com/");
        var china = TimeZoneInfo.CreateCustomTimeZone("test-China", TimeSpan.FromHours(8), "test-China", "test-China");
        Check(ResetScheduleFormatter.TryGetTime(item.Summary, posted, out var at, out var before) && before &&
              TimeZoneInfo.ConvertTime(at, china) == DateTimeOffset.Parse("2026-10-23T02:00:00+08:00"),
            "tomorrow uses the source calendar day and literal PST converts across midnight to UTC+8");
        Check(ResetScheduleFormatter.TryGetTime("Tomorrow before 10am PDT", posted, out at, out _) &&
              TimeZoneInfo.ConvertTime(at, china).Hour == 1,
            "PDT uses UTC-7 while an explicit PST remains UTC-8");
        Check(ResetScheduleFormatter.TryGetTime("Tomorrow at 10am PT", posted, out at, out before) && !before &&
              TimeZoneInfo.ConvertTime(at, china).Hour == 1,
            "generic PT observes Pacific daylight saving on the target date");
        Check(ResetScheduleFormatter.TryGetTime("Today at 12am UTC", posted, out at, out _) && at.Hour == 0 &&
              ResetScheduleFormatter.TryGetTime("Today by 12:30pm GMT", posted, out at, out before) &&
              at.Hour == 12 && at.Minute == 30 && before,
            "midnight, noon, minutes and deadline qualifiers retain their meaning");
        Check(!ResetScheduleFormatter.TryGetTime("Reset tomorrow", posted, out _, out _) &&
              !ResetScheduleFormatter.TryGetTime("10am PST", posted, out _, out _) &&
              !ResetScheduleFormatter.TryGetTime("Tomorrow at 10am UTC+8", posted, out _, out _),
            "vague or unsupported source times never use the posting time as a reset deadline");
        var previous = LocalizationService.CurrentLanguage;
        foreach (var language in new[] { LocalizationService.Chinese, LocalizationService.English })
        {
            LocalizationService.Initialize(language);
            var text = ResetScheduleFormatter.Format(item, localZone: china);
            Check(text.Contains("10-23 02:00") && text.Contains("UTC+08:00") &&
                  text.Contains(LocalizationService.IsEnglish ? "before" : "前"),
                "converted deadline labels are localized and include the destination UTC offset");
            Check(ResetScheduleFormatter.Format(item with { Kind = "announcement" }, localZone: china) == "",
                "a confirmed announcement does not retain a preview deadline");
        }
        LocalizationService.Initialize(previous);
    }

    private static void VerifyNewestResetPreview()
    {
        var now = DateTimeOffset.Parse("2026-10-22T08:04:00Z");
        var replyAt = DateTimeOffset.Parse("2026-10-22T06:55:54Z");
        using var feed = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            fetched_at = now, events = Array.Empty<object>(),
            signal = new { tweet_id = "reply", summary = "Lands tomorrow before 10am PST.",
                localized_summary = "明天上午 10 点 PST 前到达。", at = replyAt,
                url = "https://x.com/thsottiaux/status/2113162413008884099", active = true }
        }));
        foreach (var officialAt in new[] { replyAt.AddSeconds(-32), replyAt.AddSeconds(32),
                     now.AddDays(-2), now.AddHours(1) })
        {
            using var forecast = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                updated_at = now, last_reset_at = now.AddDays(-2),
                official_signal = new { tweet_id = "parent", summary = "It is reset day today.",
                    at = officialAt, url = "https://x.com/thsottiaux/status/2113162277688070445" }
            }));
            var snapshot = CodexResetClient.Parse(feed.RootElement, forecast.RootElement, now);
            var expected = officialAt == replyAt.AddSeconds(32) ? "parent" : "reply";
            Check(snapshot.Signal?.Id == expected,
                "source selection keeps the newest valid preview and ignores stale or future alternatives");
            if (expected == "reply")
                Check(ResetScheduleFormatter.Format(snapshot.Signal!, localZone:
                    TimeZoneInfo.CreateCustomTimeZone("test-China", TimeSpan.FromHours(8), "test-China", "test-China"))
                    .Contains("10-23 02:00"),
                    "the newer reply retains its converted local deadline when a forecast parent exists");
        }
    }
}
