using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    // Pump the dispatcher for real async button handlers without starting the product's
    // singleton, tray, live account connection or StartupUri window.
    private sealed class SharedUsageTestApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    private sealed class SharedUsageSyncTestHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public long LastTokens { get; private set; }
        public Uri? LastEndpoint { get; private set; }
        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastEndpoint = request.RequestUri;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var payload = body.RootElement;
            LastTokens = payload.GetProperty("totalTokens").GetInt64();
            if (Fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"success\":false,\"error\":\"Test server unavailable\"}")
            };
            var total = LastTokens + 200;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    success = true,
                    group = new { deviceCount = 2, totalTokens = total },
                    devices = new[]
                    {
                        new { deviceId = payload.GetProperty("deviceId").GetString(), deviceName = "Test PC",
                            totalTokens = LastTokens, tokenSharePercent = LastTokens * 100d / total,
                            sessionCount = payload.GetProperty("sessionCount").GetInt32(), mostUsedModel = "unknown", updatedAt = DateTimeOffset.Now },
                        new { deviceId = (string?)"other-device", deviceName = "Other PC", totalTokens = 200L,
                            tokenSharePercent = 200 * 100d / total, sessionCount = 2, mostUsedModel = "unknown", updatedAt = DateTimeOffset.Now }
                    }
                }))
            };
        }
    }

    private static void VerifySharedUsageCounter()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "shared-usage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, DateTimeOffset.Now.Offset);
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var lastWeek = new SharedUsageSample("old", today.AddDays(-1), "model-old", 100);
        var current = new SharedUsageSample("current", today, "model-new", 40);
        var currentMore = current with { Tokens = 65 };
        var samples = new[] { lastWeek, current };
        SharedUsageCounter Counter(string name) => new(Path.Combine(root, name + ".json"));
        AiLimitWindow Limit(double used, DateTimeOffset reset, int minutes = 10080, string id = "codex") => new()
        {
            LimitId = id, UsedPercent = used, ResetsAt = reset, WindowDurationMinutes = minutes
        };
        var weekly = Counter("weekly");
        var w = weekly.Update(samples, SharedUsageResetModes.Weekly, now);
        Check(w.TotalTokens == 40 && w.SessionCount == 1 && w.MostUsedModel == "model-new" &&
              w.PeriodStart == today && w.PeriodEnd == today.AddDays(6), "weekly counts only local Monday through Sunday");
        Check(weekly.Update(samples, SharedUsageResetModes.Weekly, now.AddDays(7)).TotalTokens == 0,
            "weekly starts at zero in the next week without erasing historical observations");
        Check(weekly.Update(samples, SharedUsageResetModes.Never, now.AddDays(7)).TotalTokens == 140,
            "never-clear includes available history and persists across weeks");
        var clear = Counter("clear");
        clear.Update(samples, SharedUsageResetModes.Never, now);
        Check(clear.Update(samples, SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Clear).TotalTokens == 0 && clear.CanRestore,
            "manual clear establishes an undoable local baseline");
        clear = Counter("clear");
        Check(clear.CanRestore && clear.Update(new[] { lastWeek, currentMore }, SharedUsageResetModes.Never, now).TotalTokens == 25,
            "clear and undo survive restart; only post-clear usage is counted");
        var restored = clear.Update(new[] { lastWeek, currentMore }, SharedUsageResetModes.Never, now,
            action: SharedUsageCounterAction.Restore);
        Check(restored.TotalTokens == 165 && restored.SessionCount == 2 && restored.MostUsedModel == "model-old" && !clear.CanRestore,
            "restore keeps prior and subsequent tokens, sessions and top model");
        Check(Counter("clear").Update([], SharedUsageResetModes.Never, now).TotalTokens == 165,
            "deleting logs does not erase already collected usage");
        Check(clear.Update(new[] { current }, SharedUsageResetModes.Never, now).TotalTokens == 165,
            "truncated logs and repeated observations cannot reduce or duplicate usage");
        Check(Counter("clear").Update([], SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Restore).TotalTokens == 165,
            "restore without an undo record is a no-op");
        clear.Update([], SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Clear);
        clear.Update(new[] { current with { Tokens = 70 } }, SharedUsageResetModes.Never, now);
        clear.Update([], SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Clear);
        Check(clear.Update([], SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Restore).TotalTokens == 5,
            "multiple clears undo only the most recent clear");
        var weekClear = Counter("week-clear");
        weekClear.Update(samples, SharedUsageResetModes.Weekly, now, action: SharedUsageCounterAction.Clear);
        Check(weekClear.Update(new[] { currentMore }, SharedUsageResetModes.Weekly, now,
                action: SharedUsageCounterAction.Restore).TotalTokens == 65,
            "restore in weekly mode does not resurrect last week's tokens");
        var reset = Counter("reset");
        Check(reset.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(80, now.AddDays(2)) }).TotalTokens == 140 && !reset.CanRestore,
            "initial official account reading establishes a baseline without clearing");
        reset = Counter("reset");
        Check(reset.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(0, now.AddDays(7)) }).TotalTokens == 0 && reset.CanRestore,
            "temporary official weekly recovery clears even after restart");
        Check(reset.Update(new[] { currentMore }, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(0, now.AddDays(7)) }).TotalTokens == 25,
            "same recovered quota window is not cleared twice");
        Check(reset.Update([], SharedUsageResetModes.AccountReset, now,
            action: SharedUsageCounterAction.Restore).TotalTokens == 165,
            "automatic clear can be restored with newly accrued usage");
        var scheduled = Counter("scheduled");
        scheduled.Update(samples, SharedUsageResetModes.AccountReset, now, new[] { Limit(5, now.AddHours(1)) });
        Check(scheduled.Update(samples, SharedUsageResetModes.AccountReset, now.AddHours(2),
            new[] { Limit(0, now.AddDays(7)) }).TotalTokens == 0,
            "scheduled weekly expiry clears even when previous usage was low");
        var unrelated = Counter("unrelated");
        unrelated.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(80, now.AddHours(1), 300), Limit(80, now.AddDays(2), id: "model-bucket") });
        Check(unrelated.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(0, now.AddHours(5), 300), Limit(0, now.AddDays(7), id: "model-bucket") }).TotalTokens == 140,
            "short quota windows and model-specific buckets do not clear account-wide shared usage");
        var corrected = Counter("correction");
        corrected.Update(samples, SharedUsageResetModes.AccountReset, now, new[] { Limit(80, now.AddDays(2)) });
        Check(corrected.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(70, now.AddDays(7)) }).TotalTokens == 140,
            "quota percentage or future date corrections alone do not clear");
        foreach (var mode in new[] { SharedUsageResetModes.Weekly, SharedUsageResetModes.Never })
        {
            var unaffected = Counter("policy-" + mode);
            unaffected.Update(samples, mode, now, new[] { Limit(80, now.AddDays(2)) });
            Check(unaffected.Update(samples, mode, now, new[] { Limit(0, now.AddDays(7)) }).TotalTokens ==
                  (mode == SharedUsageResetModes.Weekly ? 40 : 140), "account recovery respects " + mode + " policy");
        }
        var another = Counter("another-device");
        another.Update(samples, SharedUsageResetModes.Never, now);
        reset.Update([], SharedUsageResetModes.AccountReset, now, action: SharedUsageCounterAction.Clear);
        Check(another.Update([], SharedUsageResetModes.Never, now).TotalTokens == 140,
            "clearing a device never changes another device's local ledger");
        var duplicate = Counter("duplicate");
        Check(duplicate.Update(new[] { current, current }, SharedUsageResetModes.Never, now).TotalTokens == 40,
            "moving or duplicating a rollout into archives does not count it twice");
        var failure = Counter("failure");
        failure.Update(samples, SharedUsageResetModes.Never, now);
        using (var locked = new FileStream(Path.Combine(root, "failure.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var rejected = false;
            try { failure.Update(samples, SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Clear); }
            catch (IOException) { rejected = true; }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check(rejected && !failure.CanRestore && failure.Summarize(SharedUsageResetModes.Never, now).TotalTokens == 140,
                "atomic save failure cannot clear counts or install an undo record in memory");
        }
        File.WriteAllText(Path.Combine(root, "corrupt.json"), "{invalid");
        var corruptRejected = false;
        try { Counter("corrupt"); } catch (JsonException) { corruptRejected = true; }
        Check(corruptRejected, "corrupt state is reported instead of silently resetting counts");
        var settings = new AiManagerSettings { SharedUsageDeviceId = "this-device", SharedUsageDeviceName = "Test device" };
        using var upload = JsonDocument.Parse(SharedUsageClient.SerializeUpload(settings, restored));
        Check(upload.RootElement.GetProperty("deviceId").GetString() == "this-device" &&
              upload.RootElement.GetProperty("totalTokens").GetInt64() == 165 &&
              upload.RootElement.GetProperty("periodStart").GetString() == "2026-10-12" &&
              !upload.RootElement.TryGetProperty("baseline", out _) && !upload.RootElement.TryGetProperty("samples", out _),
            "upload retains only the existing current-device summary contract");
        Check(new AiManagerSettings().SharedUsageResetMode == SharedUsageResetModes.Never &&
              JsonSerializer.Deserialize<AiManagerSettings>("{}")!.SharedUsageResetMode == SharedUsageResetModes.Never &&
              SharedUsageResetModes.Normalize(null) == SharedUsageResetModes.Never &&
              SharedUsageResetModes.Normalize("invalid") == SharedUsageResetModes.Never &&
              SharedUsageResetModes.Normalize(SharedUsageResetModes.Weekly) == SharedUsageResetModes.Weekly &&
              SharedUsageResetModes.Normalize(SharedUsageResetModes.AccountReset) == SharedUsageResetModes.AccountReset,
            "new or missing settings default to never-clear while saved choices are preserved");

        var app = new SharedUsageTestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        void WaitForSharedOperation(Button completion)
        {
            if (completion.IsEnabled) return;
            var frame = new DispatcherFrame();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (completion.IsEnabled || DateTime.UtcNow >= deadline) frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Check(completion.IsEnabled, "shared usage button finishes its asynchronous operation");
        }
        void ClickAndWait(Button button, Button? completionButton = null)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForSharedOperation(completionButton ?? button);
        }
        foreach (var language in new[] { LocalizationService.Chinese, LocalizationService.English })
        {
            LocalizationService.Initialize(language);
            var deviceDirectory = Path.Combine(root, language);
            var sourceDirectory = Path.Combine(deviceDirectory, "codex");
            var sessionDirectory = Path.Combine(sourceDirectory, "sessions");
            Directory.CreateDirectory(sessionDirectory);
            var settingsPath = Path.Combine(deviceDirectory, "settings.json");
            var deviceSettings = new AiManagerSettings { Theme = ThemeModes.Dark, SharedUsageDeviceName = "Test PC" };
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(deviceSettings));
            var logPath = Path.Combine(sessionDirectory, "rollout-test.jsonl");
            string Record(long tokens) => JsonSerializer.Serialize(new
            {
                type = "token_usage_record", timestamp = DateTimeOffset.Now,
                payload = new { usage = new { total_tokens = tokens } }
            }) + "\n";
            File.WriteAllText(logPath, Record(40));
            var service = new AiManagerService(settingsPath, sourceDirectory);
            var window = new AiManagerWindow(service);
            window.LoadSharedUsageControls();
            var card = (FrameworkElement)window.FindName("SharedUsageCard");
            var radio = (RadioButton)window.FindName("SharedUsageAccountResetRadio");
            var clearButton = (Button)window.FindName("SharedUsageClearButton");
            var restoreButton = (Button)window.FindName("SharedUsageRestoreButton");
            var saveButton = (Button)window.FindName("SharedUsageSaveButton");
            var neverRadio = (RadioButton)window.FindName("SharedUsageNeverRadio");
            foreach (var selected in new[] { SharedUsageResetModes.AccountReset, SharedUsageResetModes.Never, SharedUsageResetModes.Weekly })
            {
                ((RadioButton)window.FindName(selected == SharedUsageResetModes.AccountReset ? "SharedUsageAccountResetRadio"
                    : selected == SharedUsageResetModes.Never ? "SharedUsageNeverRadio" : "SharedUsageWeeklyRadio")).IsChecked = true;
                ClickAndWait(saveButton);
                Check(service.ReloadSettings().SharedUsageResetMode == selected, "Save & sync persists selected policy " + selected);
            }
            neverRadio.IsChecked = true;
            ClickAndWait(clearButton);
            var deviceCounterPath = Path.Combine(deviceDirectory, "shared-usage-" + deviceSettings.SharedUsageDeviceId + ".json");
            Check(restoreButton.IsEnabled && new SharedUsageCounter(deviceCounterPath).Summarize(SharedUsageResetModes.Never, DateTimeOffset.Now).TotalTokens == 0,
                "real clear button saves only this device's zero baseline while sharing is disabled");
            File.AppendAllText(logPath, Record(25));
            var archiveDirectory = Path.Combine(sourceDirectory, "archived_sessions");
            Directory.CreateDirectory(archiveDirectory);
            File.Copy(logPath, Path.Combine(archiveDirectory, Path.GetFileName(logPath)));
            ClickAndWait(restoreButton, clearButton);
            Check(!restoreButton.IsEnabled && new SharedUsageCounter(deviceCounterPath).Summarize(SharedUsageResetModes.Never, DateTimeOffset.Now).TotalTokens == 65,
                "real restore button rescans new JSONL records and does not duplicate archived copies");
            var localToday = DateOnly.FromDateTime(DateTime.Now);
            var localMonday = localToday.AddDays(-(((int)localToday.DayOfWeek + 6) % 7));
            Check(service.ReadLocalUsageSummary(sessionDirectory, localMonday).TotalTokens == 65,
                "clear and restore leave the dashboard's original local usage intact");
            Check(radio.Content?.ToString() == (language == LocalizationService.English ? "Clear on reset" : "重置时清零") &&
                  clearButton.Content?.ToString() == (language == LocalizationService.English ? "Clear manually" : "手动清零") &&
                  restoreButton.Content?.ToString() == (language == LocalizationService.English ? "Restore" : "恢复"),
                "shared usage policy and clear/restore controls are localized in " + language);
            card.Measure(new Size(420, double.PositiveInfinity));
            card.Arrange(new Rect(0, 0, 420, card.DesiredSize.Height));
            card.UpdateLayout();
            Check(clearButton.ActualHeight >= 30 && restoreButton.ActualHeight >= 30 && radio.ActualWidth > 0,
                "new shared usage controls receive visible layout space in " + language);
            var image = new RenderTargetBitmap(420, (int)Math.Ceiling(card.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(card);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(root, "shared-usage-" + language + ".png"))) encoder.Save(output);
            window.Close();
        }
        var switchingDirectory = Path.Combine(root, "switching");
        var switchingSource = Path.Combine(switchingDirectory, "codex");
        var switchingSessions = Path.Combine(switchingSource, "sessions");
        Directory.CreateDirectory(switchingSessions);
        var switchingSettingsPath = Path.Combine(switchingDirectory, "settings.json");
        var switchingSettings = new AiManagerSettings
        {
            SharedUsageEnabled = true, SharedUsageDeviceName = "Test PC",
            SharedUsageServerUrl = "https://sync.example.test", SharedUsageSyncKeyProtected = LocalSecretProtection.Protect(new string('x', 32))
        };
        File.WriteAllText(switchingSettingsPath, JsonSerializer.Serialize(switchingSettings));
        string SwitchingRecord(long tokens, DateTimeOffset at) => JsonSerializer.Serialize(new
        {
            type = "token_usage_record", timestamp = at, payload = new { usage = new { total_tokens = tokens } }
        }) + "\n";
        var switchingNow = DateTimeOffset.Now;
        var switchingToday = DateOnly.FromDateTime(switchingNow.LocalDateTime);
        var switchingMonday = switchingToday.AddDays(-(((int)switchingToday.DayOfWeek + 6) % 7));
        var switchingLog = Path.Combine(switchingSessions, "current.jsonl");
        File.WriteAllText(switchingLog, SwitchingRecord(40, switchingNow));
        File.WriteAllText(Path.Combine(switchingSessions, "old.jsonl"), SwitchingRecord(100,
            new DateTimeOffset(switchingMonday.AddDays(-1).ToDateTime(TimeOnly.MinValue), switchingNow.Offset)));
        var handler = new SharedUsageSyncTestHandler();
        var switchingService = new AiManagerService(switchingSettingsPath, switchingSource, new SharedUsageClient(handler));
        var switchingWindow = new AiManagerWindow(switchingService);
        switchingWindow.LoadSharedUsageControls();
        var switchSave = (Button)switchingWindow.FindName("SharedUsageSaveButton");
        var switchClear = (Button)switchingWindow.FindName("SharedUsageClearButton");
        var switchRestore = (Button)switchingWindow.FindName("SharedUsageRestoreButton");
        var switchItems = (ItemsControl)switchingWindow.FindName("SharedDeviceItems");
        Check(handler.RequestCount == 0 && !File.Exists(Path.Combine(switchingDirectory,
                "shared-usage-" + switchingSettings.SharedUsageDeviceId + ".json")),
            "loading saved radio choices does not save, scan or upload usage");
        // Keep connection edits pending while switching only the count policy.
        ((TextBox)switchingWindow.FindName("SharedUsageServerUrlBox")).Text = "https://unsaved.example.test";
        foreach (var mode in new[] { SharedUsageResetModes.Weekly, SharedUsageResetModes.Never, SharedUsageResetModes.AccountReset })
        {
            var before = handler.RequestCount;
            ((RadioButton)switchingWindow.FindName(mode == SharedUsageResetModes.Weekly ? "SharedUsageWeeklyRadio"
                : mode == SharedUsageResetModes.Never ? "SharedUsageNeverRadio" : "SharedUsageAccountResetRadio")).IsChecked = true;
            WaitForSharedOperation(switchSave);
            var expected = mode == SharedUsageResetModes.Weekly ? 40 : 140;
            var displayed = switchItems.Items.Cast<AiDeviceUsage>().Single(item => item.IsCurrentDevice);
            Check(handler.RequestCount == before + 1 && handler.LastTokens == expected && displayed.TotalTokens == expected &&
                  Math.Abs(displayed.TokenSharePercent - expected * 100d / (expected + 200)) < 0.01 &&
                  switchingService.ReloadSettings().SharedUsageResetMode == mode,
                "switching to " + mode + " immediately saves, uploads and refreshes usage and percentage without Save & sync");
            Check(switchItems.Items.Cast<AiDeviceUsage>().Single(item => !item.IsCurrentDevice).TotalTokens == 200 &&
                  handler.LastEndpoint!.Host == "sync.example.test",
                "policy switching retains other-device counts and pending connection edits");
        }
        ClickAndWait(switchClear);
        File.AppendAllText(switchingLog, SwitchingRecord(25, switchingNow));
        ((RadioButton)switchingWindow.FindName("SharedUsageNeverRadio")).IsChecked = true;
        WaitForSharedOperation(switchSave);
        Check(switchItems.Items.Cast<AiDeviceUsage>().Single(item => item.IsCurrentDevice).TotalTokens == 25 && switchRestore.IsEnabled,
            "switching policies rescans newly added usage and keeps the existing clear and undo baseline");
        ClickAndWait(switchRestore, switchSave);
        Check(switchItems.Items.Cast<AiDeviceUsage>().Single(item => item.IsCurrentDevice).TotalTokens == 165,
            "restore still includes prior and subsequent usage after switching policies");
        handler.Fail = true;
        ((RadioButton)switchingWindow.FindName("SharedUsageWeeklyRadio")).IsChecked = true;
        WaitForSharedOperation(switchSave);
        Check(switchingService.ReloadSettings().SharedUsageResetMode == SharedUsageResetModes.Weekly &&
              ((TextBlock)switchingWindow.FindName("SharedUsageStatusText")).Text.Contains("Test server unavailable"),
            "sync failure preserves the selected policy and displays a retryable error");
        switchingWindow.Close();
        app.ExitApplication();
        Console.WriteLine("Shared usage test artifacts: " + root);
    }
}
