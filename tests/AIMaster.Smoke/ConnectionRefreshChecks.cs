using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyConnectionRefreshChecks()
    {
        VerifyConnectionRefreshChecksAsync().GetAwaiter().GetResult();
        var app = new App();
        app.InitializeComponent();
        LocalizationService.Initialize(LocalizationService.English);
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

    private static async Task VerifyConnectionRefreshChecksAsync()
    {
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
}
