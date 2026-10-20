using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyDashboardCardContent(string[] args)
    {
        var output = args.SkipWhile(arg => arg != "--output").Skip(1).FirstOrDefault();
        var directory = Path.Combine(Path.GetTempPath(), "aimaster-layout-" + Guid.NewGuid().ToString("N"));
        var service = new AiManagerService(Path.Combine(directory, "settings.json"));
        // Reproduce large fixed-height preferences from the previous spacious layout.
        foreach (var entry in AiManagerWindow.NormalizeDashboardLayout(null))
            service.Settings.DashboardCardSizes[entry.Split(':')[1]] = new AiDashboardCardSize { Width = 430, Height = 1000 };
        var main = new AiManagerWindow(service);
        Check(((Border)main.FindName("QuotaCard")).MinHeight == 0,
            "legacy large heights do not add blank space to compact cards");
        VerifyDashboardCardResize(main);
        VerifyProjectUsageTemplate(main);
        VerifySharedUsageTemplate(main);
        var now = DateTimeOffset.Parse("2026-10-15T06:13:00Z");
        var summary = string.Join(" ", Enumerable.Repeat("Long announcement text must remain fully visible without a nested scrollbar.", 8));
        main.RenderResetSignals(new CodexResetSnapshot(
            Enumerable.Range(0, 3).Select(index => new CodexResetEvent(index.ToString(), "announcement", now.AddHours(-index),
                summary, "https://codex-reset.com/", string.Concat(Enumerable.Repeat("完整公告自动换行撑高卡片，不需要在卡片内部滚动。", 8)))).ToArray(),
            null, now, now, now, false));
        var history = (Expander)main.FindName("ResetHistoryExpander");
        Check(((ItemsControl)main.FindName("ResetSignalItems")).Items.Count == 1 &&
              ((ItemsControl)main.FindName("ResetHistoryItems")).Items.Count == 2 && !history.IsExpanded,
            "the newest announcement is visible and earlier announcements remain available on demand");
        history.IsExpanded = true;

        foreach (var language in new[] { LocalizationService.English, LocalizationService.Chinese })
        {
            LocalizationService.SetLanguage(language);
            foreach (var width in new[] { 280d, 430d, 650d })
            foreach (var entry in main.CaptureDashboardLayout())
            {
                var id = entry.Split(':')[1];
                var card = Descendants(main).OfType<Border>().Single(item => Equals(item.Tag, id));
                card.Measure(new Size(width, double.PositiveInfinity));
                card.Arrange(new Rect(0, 0, width, card.DesiredSize.Height));
                card.UpdateLayout();
                var frame = (Grid)card.Child;
                var content = frame.Children.OfType<Border>().Single();
                var bottom = content.TranslatePoint(new Point(0, content.ActualHeight), card).Y;
                Check(double.IsNaN(card.Height) && !frame.Children.OfType<ScrollViewer>().Any() &&
                      bottom <= card.ActualHeight - card.Padding.Bottom + 1,
                    $"{language} {id} at {width}px follows its content instead of the legacy saved height");
                if (id != "resetSignals") continue;
                var footer = (TextBlock)main.FindName("ResetCheckedText");
                var footerBottom = footer.TranslatePoint(new Point(0, footer.ActualHeight), card).Y;
                Check(card.ActualHeight > 180 && footerBottom <= card.ActualHeight - card.Padding.Bottom + 1,
                    $"{language} {width}px long announcements and footer are fully visible without scrolling the card");
                if (output is null) continue;
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap((int)width, (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(card);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, $"reset-autosize-{language}-{width}.png"));
                encoder.Save(stream);
            }
        }
        service.Settings.DashboardCardSizingVersion = 1;
        service.Settings.DashboardCardSizes["quota"] = new AiDashboardCardSize { Width = 430, Height = 500 };
        typeof(AiManagerWindow).GetMethod("ApplyDashboardLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
        Check(((Border)main.FindName("QuotaCard")).MinHeight == 500 && double.IsNaN(((Border)main.FindName("QuotaCard")).Height),
            "new deliberate resizing retains a minimum height without fixing or clipping content");
        main.Close();
        Console.WriteLine("Automatic card sizing checks passed.");
    }
}
