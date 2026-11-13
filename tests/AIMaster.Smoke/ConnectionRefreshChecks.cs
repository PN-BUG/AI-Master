using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyConnectionRefreshChecks(bool quotaOnly = false)
    {
        VerifyConnectionRefreshChecksAsync(quotaOnly).GetAwaiter().GetResult();
        var app = new App();
        app.InitializeComponent();
        LocalizationService.Initialize(LocalizationService.English);
        VerifyFloatingQuotaUi();
        if (quotaOnly)
        {
            app.ExitApplication();
            return;
        }
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster-quota-state-" + Guid.NewGuid().ToString("N"));
        var service = new AiManagerService(Path.Combine(directory, "settings.json"));
        var main = new AiManagerWindow(service, new ResetSignalAttention(Path.Combine(directory, "attention.json")));
        try
        {
            var errorField = typeof(AiManagerService).GetField("<LastQuotaError>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(errorField is not null, "quota sync failures have an explicit service state");
            errorField!.SetValue(service, "failed to fetch codex rate limits: error sending request for url (https://chatgpt.com/backend-api/wham/usage)");
            var snapshot = new AiManagerSnapshot { Limits = [new AiLimitWindow
            {
                LimitId = "codex", UsedPercent = 99, IsPrimary = true,
                WindowDurationMinutes = 10080, ResetsAt = DateTimeOffset.Now.AddDays(3)
            }] };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AiManagerWindow).GetMethod("RenderSnapshot", flags)!.Invoke(main, [snapshot]);
            Check(main.FindName("QuotaStateText") is TextBlock { Text: "Cached quota" } &&
                  main.FindName("SyncStatusText") is TextBlock { Text: "Quota sync unavailable" },
                "cached quota remains visible with an explicit warning instead of a false connected state");
            CaptureQuotaFailure("en-US");
            typeof(AiManagerWindow).GetMethod("ApplyQuotaNotification", flags)!.Invoke(main, [snapshot]);
            ((Task)typeof(AiManagerWindow).GetMethod("ApplyGuardAsync", flags)!.Invoke(main, [snapshot])!).GetAwaiter().GetResult();
            Check(!(bool)typeof(AiManagerWindow).GetField("_localGuardPaused", flags)!.GetValue(main)!,
                "an old high quota reading cannot trigger automatic task interruption after sync fails");
            typeof(AiManagerWindow).GetMethod("ObserveAccountRecovery", flags)!.Invoke(main, [snapshot]);
            Check(!File.Exists(service.AccountResetStatePath), "failed quota sync cannot advance official recovery history");
            LocalizationService.SetLanguage(LocalizationService.Chinese);
            typeof(AiManagerWindow).GetMethod("RenderSnapshot", flags)!.Invoke(main, [snapshot]);
            Check(main.FindName("QuotaStateText") is TextBlock { Text: "缓存额度" } &&
                  main.FindName("SyncStatusText") is TextBlock { Text: "额度同步暂不可用" },
                "cached quota warnings also remain explicit in Chinese");
            CaptureQuotaFailure("zh-CN");
            errorField.SetValue(service, null);
            typeof(AiManagerWindow).GetMethod("RenderSnapshot", flags)!.Invoke(main, [snapshot]);
            Check(((Border)main.FindName("QuotaStateBadge")).ToolTip is null &&
                  main.FindName("QuotaStateText") is TextBlock { Text: "暂停线" },
                "a successful quota update clears stale badge text and its old failure tooltip");

            void CaptureQuotaFailure(string language)
            {
                var card = (Border)main.FindName("QuotaCard");
                card.Measure(new System.Windows.Size(520, double.PositiveInfinity));
                card.Arrange(new System.Windows.Rect(0, 0, 520, 320));
                card.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight), 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(card);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var output = Path.GetFullPath(Path.Combine("artifacts", "connection-stability-ui"));
                Directory.CreateDirectory(output);
                using var stream = File.Create(Path.Combine(output, $"quota-stale-{language}.png"));
                encoder.Save(stream);
            }
        }
        finally
        {
            main.Close();
            app.ExitApplication();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyConnectionRefreshChecksAsync(bool quotaOnly = false)
    {
        await WithConnectionFixtureAsync("quota-snapshots", async (_, directory) =>
        {
            var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            try
            {
                void SetQuota(int weeklyUsed) => File.WriteAllText(Path.Combine(directory, "quota.json"),
                    JsonSerializer.Serialize(new { rateLimits = new { limitId = "codex",
                        primary = new { usedPercent = 0, windowDurationMins = 300,
                            resetsAt = DateTimeOffset.Now.AddHours(5).ToUnixTimeSeconds() },
                        secondary = new { usedPercent = weeklyUsed, windowDurationMins = 10080,
                            resetsAt = DateTimeOffset.Now.AddDays(4).ToUnixTimeSeconds() } } }));
                int Reads() => Directory.GetFiles(directory, "*.requests").SelectMany(File.ReadAllLines)
                    .Count(method => method == "account/rateLimits/read");
                await using var runtime = new AiManagerRuntime();
                await using var main = new AiManagerService(Path.Combine(directory, "settings.json"), runtime: runtime);
                await using var floating = new AiManagerService(Path.Combine(directory, "settings.json"), runtime: runtime);
                SetQuota(80);
                await main.RefreshAsync();
                var first = await floating.RefreshFloatingAsync();
                Check(first.RemainingPercent == 20 && first.FiveHourQuota?.RemainingPercent == 100 &&
                      first.WeeklyQuota?.RemainingPercent == 20 && Reads() == 1,
                    "floating window immediately uses the dashboard quota without a duplicate read");
                var stale = runtime.LatestQuota!;
                SetQuota(25);
                await main.RefreshAsync();
                var refreshed = await floating.RefreshFloatingAsync();
                Check(refreshed.RemainingPercent == 75 && Reads() == 2 &&
                      refreshed.WeeklyQuota?.RemainingPercent == 75 &&
                      refreshed.QuotaCapturedAt >= first.QuotaCapturedAt,
                    "dashboard weekly quota refresh replaces the floating cache before its minute timer expires");
                runtime.PublishQuota(stale.Revision, stale.Limits, DateTimeOffset.Now.AddMinutes(1));
                Check((await floating.RefreshFloatingAsync()).RemainingPercent == 75,
                    "a late older quota response cannot overwrite the shared reading");
                SetQuota(40);
                Check((await floating.RefreshFloatingAsync(forceRefresh: true)).RemainingPercent == 60 && Reads() == 3,
                    "manual floating refresh bypasses the shared quota cache");
            }
            finally { Environment.SetEnvironmentVariable("CODEX_HOME", previousHome); }
        });
        if (quotaOnly) return;
        await WithConnectionFixtureAsync("network-always", async (_, directory) =>
        {
            var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            try
            {
                await using var service = new AiManagerService(Path.Combine(directory, "settings.json"));
                await service.RefreshFloatingAsync();
                var second = await service.RefreshFloatingAsync();
                var requests = Directory.GetFiles(directory, "*.requests").SelectMany(File.ReadAllLines).ToList();
                Check(requests.Count(method => method == "model/list") == 1 &&
                      requests.Count(method => method == "account/rateLimits/read") <= 2,
                    "floating refresh backs off failed remote reads instead of repeating them every two seconds");
                Check(typeof(AiFloatingSnapshot).GetProperty("QuotaError")?.GetValue(second) is string,
                    "floating fallback exposes its quota failure separately from current local tasks");
                var projected = AiTaskSnapshotFeed.ApplyToFloating(second, []);
                Check(Equals(typeof(AiFloatingSnapshot).GetProperty("QuotaError")?.GetValue(projected),
                        typeof(AiFloatingSnapshot).GetProperty("QuotaError")?.GetValue(second)),
                    "local task publications preserve quota failure state");
            }
            finally { Environment.SetEnvironmentVariable("CODEX_HOME", previousHome); }
        });
        await WithConnectionFixtureAsync("normal", async (_, directory) =>
        {
            var previousExecutable = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
            var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var invalidExecutable = Path.Combine(directory, "invalid.exe");
            File.WriteAllText(invalidExecutable, "This is a local invalid-executable fixture.");
            Environment.SetEnvironmentVariable("CODEX_EXECUTABLE", invalidExecutable);
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            try
            {
                var sessions = Path.Combine(directory, "sessions");
                Directory.CreateDirectory(sessions);
                var localRollout = Path.Combine(sessions, "connection-fallback.jsonl");
                File.WriteAllText(localRollout, MemoryMetadata("connection-fallback") + MemoryEvent("task_started"));
                await using var service = new AiManagerService(Path.Combine(directory, "settings.json"));
                var revision = AiTaskSnapshotFeed.Latest?.Revision ?? 0;
                var snapshot = await service.RefreshFloatingAsync();
                Check(AiTaskSnapshotFeed.Latest?.Revision > revision &&
                      AiTaskSnapshotFeed.Latest.Tasks.Any(task => task.Id == "connection-fallback" && task.Status == "inProgress") &&
                      typeof(AiFloatingSnapshot).GetProperty("QuotaError")?.GetValue(snapshot) is string,
                    "a server startup failure keeps local tasks updating and reports unavailable quota");
                revision = AiTaskSnapshotFeed.Latest!.Revision;
                File.AppendAllText(localRollout, MemoryEvent("task_complete"));
                try { await service.RefreshAsync(); }
                catch (System.ComponentModel.Win32Exception) { }
                Check(AiTaskSnapshotFeed.Latest?.Revision > revision &&
                      AiTaskSnapshotFeed.Latest.Tasks.Any(task => task.Id == "connection-fallback" && task.Status == "completed"),
                    "dashboard connection failure also publishes its independent local task snapshot");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_EXECUTABLE", previousExecutable);
                Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            }
        });
    }

    private static void VerifyFloatingQuotaUi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster-floating-quota-" + Guid.NewGuid().ToString("N"));
        var floating = new AiFloatingWindow(new AiManagerService(Path.Combine(directory, "settings.json")),
            new ResetSignalAttention(Path.Combine(directory, "attention.json")));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var render = typeof(AiFloatingWindow).GetMethod("RenderSnapshot", flags)!;
        var now = DateTimeOffset.Now;
        var snapshot = new AiFloatingSnapshot
        {
            FiveHourQuota = new AiLimitWindow { LimitId = "codex", WindowDurationMinutes = 300,
                UsedPercent = 0, ResetsAt = now.AddHours(5) },
            WeeklyQuota = new AiLimitWindow { LimitId = "codex", WindowDurationMinutes = 10080,
                UsedPercent = 13, ResetsAt = now.AddDays(4) },
            ModelName = "GPT-5.4", ReasoningEffort = "high", QuotaCapturedAt = now,
            TaskName = "Quota monitor", TaskStatus = "inProgress"
        };
        try
        {
            var projected = AiTaskSnapshotFeed.ApplyToFloating(snapshot, []);
            Check(ReferenceEquals(projected.FiveHourQuota, snapshot.FiveHourQuota) &&
                  ReferenceEquals(projected.WeeklyQuota, snapshot.WeeklyQuota),
                "local task updates preserve both independently refreshed quota windows");
            foreach (var language in new[] { LocalizationService.Chinese, LocalizationService.English })
            foreach (var scale in new[] { 1d, 1.3d })
            {
                LocalizationService.Initialize(language);
                typeof(AiFloatingWindow).GetMethod("ApplyFontScale", flags)!.Invoke(floating, [scale]);
                render.Invoke(floating, [snapshot]);
                var fiveHour = (TextBlock)floating.FindName("RemainingQuotaText");
                var weekly = (TextBlock)floating.FindName("WeeklyQuotaText");
                Check(fiveHour.Text == "100" && weekly.Text == "87" &&
                      ((System.Windows.FrameworkElement)floating.FindName("WeeklyQuotaPanel")).Visibility == System.Windows.Visibility.Visible,
                    "floating window displays five-hour and weekly remaining quota at the same time");
                Check(((TextBlock)floating.FindName("QuotaLabelText")).Text.Contains("5h", StringComparison.Ordinal) &&
                      ((TextBlock)floating.FindName("WeeklyQuotaLabelText")).Text ==
                      (language == LocalizationService.Chinese ? "周剩余" : "Week left"),
                    "both quota windows have distinct localized labels");
                floating.Width = 200;
                floating.Height = 150;
                var surface = (System.Windows.FrameworkElement)floating.Content;                surface.Measure(new System.Windows.Size(200, 150));
                surface.Arrange(new System.Windows.Rect(0, 0, 200, 150));
                surface.UpdateLayout();
                Check(fiveHour.ActualWidth > 0 && weekly.ActualWidth > 0 &&
                      fiveHour.ActualWidth >= fiveHour.DesiredSize.Width && weekly.ActualWidth >= weekly.DesiredSize.Width &&
                      weekly.TranslatePoint(new System.Windows.Point(weekly.ActualWidth, weekly.ActualHeight), surface).X < 200,
                    "both quota numbers fit the compact window at normal and enlarged font sizes");
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(200, 150, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var output = Path.GetFullPath(Path.Combine("artifacts", "quota-reset-ui"));
                Directory.CreateDirectory(output);
                using var stream = File.Create(Path.Combine(output, $"floating-{language}-{scale:0.0}.png"));
                encoder.Save(stream);
            }
            render.Invoke(floating, [new AiFloatingSnapshot { WeeklyQuota = snapshot.WeeklyQuota, QuotaError = "offline" }]);
            Check(((TextBlock)floating.FindName("RemainingQuotaText")).Text == "--" &&
                  ((TextBlock)floating.FindName("WeeklyQuotaText")).Text == "87",
                "missing five-hour data never masquerades as weekly quota or a fresh 100 percent");
            // Both data sources' quota are shown together, each keeping its own value.
            render.Invoke(floating, [new AiFloatingSnapshot
            {
                Provider = AiProviderModes.DeepSeek,
                QuotaText = "12.30",
                CodexFiveHourQuota = snapshot.FiveHourQuota,
                CodexWeeklyQuota = snapshot.WeeklyQuota
            }]);
            Check(((System.Windows.FrameworkElement)floating.FindName("WeeklyQuotaPanel")).Visibility == System.Windows.Visibility.Visible &&
                  ((TextBlock)floating.FindName("WeeklyQuotaText")).Text == "87" &&
                  ((TextBlock)floating.FindName("DeepSeekBalanceText")).Text == "12.30",
                "DeepSeek balance mode keeps the Codex quota windows visible in the same window");
            render.Invoke(floating, [snapshot]);
            Check(((TextBlock)floating.FindName("WeeklyQuotaText")).Text == "87" &&
                  ((System.Windows.FrameworkElement)floating.FindName("WeeklyQuotaPanel")).Visibility == System.Windows.Visibility.Visible,
                "switching back from balance mode restores both Codex windows");
        }
        finally
        {
            floating.Close();
            LocalizationService.Initialize(LocalizationService.English);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
