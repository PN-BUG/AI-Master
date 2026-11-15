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
    private static void VerifyFloatingFooter(string? outputDirectory = null)
    {
        AiTaskSnapshotFeed.Reset();
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-footer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        var service = new AiManagerService(settingsPath: path);
        var window = new AiFloatingWindow(service, new ResetSignalAttention(Path.Combine(root, "attention.json")));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var render = typeof(AiFloatingWindow).GetMethod("RenderSnapshot", flags)!;
        var scale = typeof(AiFloatingWindow).GetMethod("ApplyFontScale", flags)!;
        var snapshot = new AiFloatingSnapshot
        {
            Provider = AiProviderModes.Codex,
            TaskName = "优化 AIMaster 浮窗底部布局",
            TaskStatus = "inProgress",
            ModelName = "gpt-6.1-sol",
            CodexFiveHourQuota = new AiLimitWindow { UsedPercent = 47 },
            CodexWeeklyQuota = new AiLimitWindow { UsedPercent = 24 },
            DeepSeekBalanceText = "¥0.06"
        };
        FrameworkElement Element(string name) => (FrameworkElement)window.FindName(name);
        void Paint(AiFloatingSnapshot? value = null)
        {
            render.Invoke(window, new object[] { value ?? snapshot });
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(window.Width, window.Height));
            content.Arrange(new Rect(0, 0, window.Width, window.Height));
            content.UpdateLayout();
        }
        void Toggle(string name, bool value)
        {
            var item = (MenuItem)window.FindName(name);
            item.IsChecked = value;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Paint();
        }
        void Capture(string name)
        {
            if (outputDirectory is null) return;
            Directory.CreateDirectory(outputDirectory);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render((Visual)window.Content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(outputDirectory, name + ".png"));
            png.Save(file);
        }
        try
        {
            LocalizationService.SetLanguage(LocalizationService.Chinese);
            window.Width = 236;
            Paint();
            var grid = (Grid)Element("QuotaSummaryGrid");
            var cells = new[] { Element("CodexQuotaRow"), Element("WeeklyQuotaPanel"), Element("DeepSeekQuotaRow") };
            Check(grid.ColumnDefinitions.Count == 3 && cells.All(cell => cell.Visibility == Visibility.Visible) &&
                  cells.Select(Grid.GetColumn).Distinct().Count() == 3,
                "footer aligns five-hour, weekly and balance metrics in separate equal columns");
            var top = cells[0].TranslatePoint(new Point(), grid).Y;
            Check(cells.All(cell => Math.Abs(cell.TranslatePoint(new Point(), grid).Y - top) < 0.1),
                "balance shares the quota row instead of consuming another line");
            Check(((TextBlock)Element("RemainingQuotaText")).FontSize == ((TextBlock)Element("DeepSeekBalanceText")).FontSize,
                "balance and remaining quota use the same type scale");
            Capture("footer-default");

            var expandedHeight = window.Height;
            Toggle("FooterVisibleMenuItem", false);
            Check(Element("QuotaBand").Visibility == Visibility.Collapsed &&
                  Element("QuotaDivider").Visibility == Visibility.Collapsed && window.Height < expandedHeight,
                "hiding the footer removes its divider and releases its window height");
            var hiddenHeight = window.Height;
            Paint();
            Check(window.Height == hiddenHeight, "refreshing a hidden footer does not change window height");
            Capture("footer-hidden");
            Toggle("FooterVisibleMenuItem", true);
            Check(Math.Abs(window.Height - expandedHeight) < 0.1, "showing the footer restores its previous height");

            Toggle("FooterFiveHourMenuItem", false);
            Toggle("FooterDeepSeekMenuItem", false);
            Check(grid.ColumnDefinitions.Count == 1 && Element("WeeklyQuotaPanel").Visibility == Visibility.Visible &&
                  Element("CodexQuotaRow").Visibility == Visibility.Collapsed &&
                  Element("DeepSeekQuotaRow").Visibility == Visibility.Collapsed,
                "choosing weekly quota alone fills the available footer width");
            var saved = service.ReloadSettings();
            Check(saved.FloatingFooterVisible && !saved.FloatingShowFiveHourQuota && saved.FloatingShowWeeklyQuota &&
                  !saved.FloatingShowDeepSeekQuota,
                "footer menu choices survive a settings-file reload");
            Capture("footer-weekly-only");
            Toggle("FooterWeeklyMenuItem", false);
            Check(Element("QuotaBand").Visibility == Visibility.Collapsed &&
                  Element("QuotaDivider").Visibility == Visibility.Collapsed,
                "deselecting every metric leaves no empty footer or divider");
            Toggle("FooterFiveHourMenuItem", true);
            Toggle("FooterWeeklyMenuItem", true);
            Toggle("FooterDeepSeekMenuItem", true);

            foreach (var language in new[] { LocalizationService.Chinese, LocalizationService.English })
            foreach (var fontScale in new[] { 1.0, 1.2, 1.5 })
            {
                LocalizationService.SetLanguage(language);
                window.Width = 180;
                scale.Invoke(window, new object[] { fontScale });
                Paint();
                var band = Element("QuotaBand");
                var shell = Element("Shell");
                var bottom = band.TranslatePoint(new Point(0, band.ActualHeight), shell).Y;
                Check(bottom <= shell.ActualHeight - 4 && band.ActualWidth > 0,
                    $"footer fits the minimum window width at {fontScale:P0} in {language}");
                Capture($"footer-{language}-{fontScale * 100:0}");
            }

            scale.Invoke(window, new object[] { 1.2 });
            window.Width = 236;
            var go = new AiFloatingSnapshot
            {
                Provider = AiProviderModes.DeepSeek,
                CodexFiveHourQuota = snapshot.CodexFiveHourQuota,
                CodexWeeklyQuota = snapshot.CodexWeeklyQuota,
                GoQuota = new AiGoQuotaSnapshot
                {
                    Rolling = new AiGoQuotaWindow { UsedPercent = 10 },
                    Weekly = new AiGoQuotaWindow { UsedPercent = 30 },
                    Monthly = new AiGoQuotaWindow { UsedPercent = 50 }
                }
            };
            window.Height = 200;
            Paint(go);
            Check(window.Height <= 200, "loading a Go snapshot compacts unused restored window height");
            Paint();
            Check(window.Height <= 200, "a data-only footer change keeps the window compact");
            Paint(go);
            window.Height = window.MinHeight;
            Paint(go);
            Check(Element("GoQuotaRow").Visibility == Visibility.Visible &&
                  Element("DeepSeekQuotaRow").Visibility == Visibility.Collapsed,
                "Go plan allowance replaces balance while preserving the Codex columns");
            Capture("footer-go-plan");
            service.Settings.FloatingQuotaScope = FloatingQuotaScopes.Active;
            Paint(go);
            Check(Element("QuotaSummaryGrid").Visibility == Visibility.Collapsed &&
                  Element("GoQuotaRow").Visibility == Visibility.Visible && Element("GoQuotaRow").Margin.Top == 0,
                "selected-source-only Go allowance has no empty summary row or extra gap");

            service.Settings.FloatingQuotaScope = FloatingQuotaScopes.All;
            foreach (var fontScale in new[] { 1.0, 1.2, 1.5 })
            {
                scale.Invoke(window, new object[] { fontScale });
                var tasks = Enumerable.Range(1, 5).Select(i => new AiThreadSummary
                    { Id = i.ToString(), Title = "Task " + i, Status = "completed", ModelName = "gpt-6.1-sol" }).ToList();
                var compact = new AiFloatingSnapshot { Tasks = tasks, CodexFiveHourQuota = snapshot.CodexFiveHourQuota,
                    CodexWeeklyQuota = snapshot.CodexWeeklyQuota, DeepSeekBalanceText = "¥0.06" };
                window.Height = 260;
                Paint(compact);
                Paint(compact);
                var rows = Element("TaskRowsPanel");
                var divider = Element("QuotaDivider");
                var gap = divider.TranslatePoint(new Point(), rows).Y - rows.ActualHeight;
                Check(((Panel)Element("SecondaryTasksPanel")).Children.Count == 4 && gap >= 0 && gap <= 6,
                    $"five tasks leave at most six pixels before the footer at {fontScale:P0} (gap={gap:F1})");
                Capture($"footer-compact-{fontScale * 100:0}");
            }

            File.WriteAllText(path, "{}");
            saved = service.ReloadSettings();
            Check(saved.FloatingFooterVisible && saved.FloatingShowFiveHourQuota && saved.FloatingShowWeeklyQuota &&
                  saved.FloatingShowDeepSeekQuota, "existing settings files retain all footer metrics by default");
        }
        finally
        {
            window.Close();
            LocalizationService.SetLanguage(LocalizationService.English);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
