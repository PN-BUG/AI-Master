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
        var lastWeek = new SharedUsageSample("old", today.AddDays(-1), "model-old", 100, now.AddDays(-1));
        var current = new SharedUsageSample("current", today, "model-new", 40, now.AddHours(-1));
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
        var firstPeriod = reset.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(80, now.AddDays(2)) });
        Check(firstPeriod.TotalTokens == 140 && firstPeriod.CountedFrom == now.AddDays(-5) && !reset.CanRestore,
            "first official snapshot immediately calculates from the weekly window start, without a clear");
        reset = Counter("reset");
        Check(reset.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(0, now.AddDays(7)) }).TotalTokens == 0 && !reset.CanRestore,
            "new official window excludes pre-reset records after restart without changing manual baselines");
        var postReset = new SharedUsageSample("current", today, "model-new", 25, now.AddMinutes(1));
        Check(reset.Update(new[] { postReset }, SharedUsageResetModes.AccountReset, now.AddMinutes(2),
            new[] { Limit(0, now.AddDays(7)) }).TotalTokens == 25,
            "new usage since the actual reset is included without resetting on repeated snapshots");
        Check(reset.Update([], SharedUsageResetModes.AccountReset, now.AddMinutes(2),
            action: SharedUsageCounterAction.Restore).TotalTokens == 25,
            "restore cannot move the selected time window back before the official reset");
        var scheduled = Counter("scheduled");
        scheduled.Update(samples, SharedUsageResetModes.AccountReset, now, new[] { Limit(5, now.AddHours(1)) });
        var afterExpiry = new SharedUsageSample("new", today, "model-new", 20, now.AddHours(1.5));
        Check(scheduled.Update(new[] { afterExpiry }, SharedUsageResetModes.AccountReset, now.AddHours(2),
            new[] { Limit(0, now.AddDays(7).AddHours(1)) }).TotalTokens == 20,
            "delayed reset detection includes usage accrued after expiry and before detection");
        var unrelated = Counter("unrelated");
        unrelated.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(80, now.AddHours(1), 300), Limit(80, now.AddDays(2), id: "model-bucket") });
        Check(unrelated.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(0, now.AddHours(5), 300), Limit(0, now.AddDays(7), id: "model-bucket") }).Error is not null,
            "short and model-specific windows cannot stand in for the account's last weekly reset");
        var corrected = Counter("correction");
        corrected.Update(samples, SharedUsageResetModes.AccountReset, now, new[] { Limit(80, now.AddDays(2)) });
        Check(corrected.Update(samples, SharedUsageResetModes.AccountReset, now,
            new[] { Limit(70, now.AddDays(7)) }).CountedFrom == now && !corrected.CanRestore,
            "official boundary changes recalculate the interval without modifying manual clear history");
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

        var boundary = now.AddHours(-3);
        var timed = new[]
        {
            lastWeek,
            new SharedUsageSample("early", today, "before", 10, boundary.AddTicks(-1)),
            new SharedUsageSample("edge", today, "after", 20, boundary),
            new SharedUsageSample("late", today, "after", 30, now.AddMinutes(-1)),
            new SharedUsageSample("future", today, "future", 999, now.AddMinutes(1))
        };
        var intervals = Counter("intervals");
        var quota = new[] { Limit(15, boundary.AddDays(7)) };
        Check(intervals.Update(timed, SharedUsageResetModes.Never, now, quota).TotalTokens == 160,
            "cumulative mode includes prior history but excludes future-dated events");
        Check(intervals.Update(timed, SharedUsageResetModes.Weekly, now, quota).TotalTokens == 60,
            "switching to weekly recalculates Monday to now from the same history");
        var sinceReset = intervals.Update(timed, SharedUsageResetModes.AccountReset, now, quota);
        Check(sinceReset.TotalTokens == 50 && sinceReset.SessionCount == 2 && sinceReset.MostUsedModel == "after" && sinceReset.CountedFrom == boundary,
            "reset mode excludes same-day pre-reset events, includes the exact boundary and refreshes sessions and model");
        Check(Counter("intervals").Update([], SharedUsageResetModes.AccountReset, now).TotalTokens == 50,
            "persisted official reset boundary supports calculating the interval after restart");
        intervals.Update(timed, SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Clear);
        Check(intervals.Update(timed, SharedUsageResetModes.Weekly, now).TotalTokens == 60 &&
              intervals.Update(timed, SharedUsageResetModes.AccountReset, now).TotalTokens == 50 &&
              !intervals.CanRestoreMode(SharedUsageResetModes.Weekly) && intervals.CanRestoreMode(SharedUsageResetModes.Never),
            "a cumulative manual clear does not leak into weekly or reset-period calculations");
        Check(intervals.Update(timed, SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Restore).TotalTokens == 160,
            "returning to a policy can restore its own manual clear");
        Check(intervals.Update([], SharedUsageResetModes.AccountReset, now.AddDays(8)).Error is not null,
            "an expired cached quota window is reported instead of reusing a stale reset boundary");
        Check(Counter("unknown-boundary").Update(timed, SharedUsageResetModes.AccountReset, now).Error is not null,
            "unknown reset time is explicit and never presented as lifetime usage");
        Check(Counter("unknown-record-time").Update(new[] { current with { Timestamp = null } }, SharedUsageResetModes.AccountReset, now, quota).Error is not null,
            "missing timestamps on reset day cannot silently cross an intraday boundary");
        var migratedDay = new SharedUsageSample("migrated", today, "model", 100);
        var legacyPath = Path.Combine(root, "legacy.json");
        var legacyJson = JsonSerializer.Serialize(new
        {
            Samples = new System.Collections.Generic.Dictionary<string, SharedUsageSample> { [migratedDay.Key] = migratedDay },
            Baseline = new System.Collections.Generic.Dictionary<string, long> { [migratedDay.Key] = 60 },
            UndoBaseline = new System.Collections.Generic.Dictionary<string, long>(),
            PreviousLimits = new System.Collections.Generic.Dictionary<string, AiLimitWindow>()
        });
        File.WriteAllText(legacyPath, legacyJson);
        var migratedEvents = new[]
        {
            migratedDay with { Tokens = 60, Timestamp = boundary.AddHours(-1) },
            migratedDay with { Tokens = 40, Timestamp = now.AddMinutes(-2) },
            migratedDay with { Tokens = 5, Timestamp = now.AddMinutes(-1) }
        };
        var migrated = new SharedUsageCounter(legacyPath);
        Check(migrated.RequiresTimestampMigration && migrated.Update(migratedEvents, SharedUsageResetModes.Never, now).TotalTokens == 45 &&
              !migrated.RequiresTimestampMigration && migrated.CanRestoreMode(SharedUsageResetModes.Never),
            "old daily ledger migrates to event timestamps without duplicating tokens or losing the manual offset");
        Check(migrated.Update(migratedEvents, SharedUsageResetModes.AccountReset, now, quota).TotalTokens == 45 &&
              migrated.Update(migratedEvents, SharedUsageResetModes.Never, now, action: SharedUsageCounterAction.Restore).TotalTokens == 105,
            "migration preserves undo and enables intraday reset calculations without sharing manual offsets");
        var partialPath = Path.Combine(root, "legacy-partial.json");
        File.WriteAllText(partialPath, legacyJson);
        File.SetLastWriteTimeUtc(partialPath, now.UtcDateTime);
        var partial = new SharedUsageCounter(partialPath);
        Check(partial.Update(new[] { migratedEvents[1] }, SharedUsageResetModes.Never, now).TotalTokens == 40,
            "partial migration retains unreadable old usage and its manual clear offset");
        var recoveredEvents = new[] { migratedEvents[0], migratedEvents[1], migratedDay with { Tokens = 5, Timestamp = now.AddMinutes(1) } };
        partial = new SharedUsageCounter(partialPath);
        Check(partial.Update(recoveredEvents, SharedUsageResetModes.Never, now.AddMinutes(2)).TotalTokens == 45 &&
              partial.Update(recoveredEvents, SharedUsageResetModes.Never, now.AddMinutes(2), action: SharedUsageCounterAction.Restore).TotalTokens == 105,
            "later log recovery replaces daily residuals without recounting old usage or discarding genuine new usage");

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
        void CaptureSharedCard(AiManagerWindow window, string fileName)
        {
            var card = (FrameworkElement)window.FindName("SharedUsageCard");
            card.Measure(new Size(420, double.PositiveInfinity));
            card.Arrange(new Rect(0, 0, 420, card.DesiredSize.Height));
            card.UpdateLayout();
            var bitmap = new RenderTargetBitmap(420, (int)Math.Ceiling(card.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(card);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(root, fileName));
            encoder.Save(output);
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
        File.WriteAllText(switchingLog, SwitchingRecord(10, switchingNow.AddTicks(-2)) + SwitchingRecord(30, switchingNow));
        File.WriteAllText(Path.Combine(switchingSessions, "old.jsonl"), SwitchingRecord(100,
            new DateTimeOffset(switchingMonday.AddDays(-1).ToDateTime(TimeOnly.MinValue), switchingNow.Offset)));
        var handler = new SharedUsageSyncTestHandler();
        var switchingBoundary = switchingNow.AddTicks(-1);
        var switchingLimits = new AiLimitWindow[]
        {
            new() { LimitId = "codex", WindowDurationMinutes = 10080, ResetsAt = switchingBoundary.AddDays(7), UsedPercent = 15 }
        };
        var switchingService = new AiManagerService(switchingSettingsPath, switchingSource, new SharedUsageClient(handler),
            _ => Task.FromResult<System.Collections.Generic.IReadOnlyList<AiLimitWindow>>(switchingLimits));
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
        foreach (var mode in new[] { SharedUsageResetModes.Weekly, SharedUsageResetModes.AccountReset, SharedUsageResetModes.Never })
        {
            var before = handler.RequestCount;
            ((RadioButton)switchingWindow.FindName(mode == SharedUsageResetModes.Weekly ? "SharedUsageWeeklyRadio"
                : mode == SharedUsageResetModes.Never ? "SharedUsageNeverRadio" : "SharedUsageAccountResetRadio")).IsChecked = true;
            WaitForSharedOperation(switchSave);
            var expected = mode == SharedUsageResetModes.Weekly ? 40 : mode == SharedUsageResetModes.AccountReset ? 30 : 140;
            var displayed = switchItems.Items.Cast<AiDeviceUsage>().Single(item => item.IsCurrentDevice);
            var periodText = ((TextBlock)switchingWindow.FindName("SharedUsagePeriodText")).Text;
            var expectedPeriod = mode == SharedUsageResetModes.Weekly ? switchingMonday.ToString("yyyy-MM-dd") + " 00:00"
                : mode == SharedUsageResetModes.AccountReset ? switchingBoundary.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : "all local history";
            Check(handler.RequestCount == before + 1 && handler.LastTokens == expected && displayed.TotalTokens == expected &&
                  Math.Abs(displayed.TokenSharePercent - expected * 100d / (expected + 200)) < 0.01 &&
                  switchingService.ReloadSettings().SharedUsageResetMode == mode && periodText.Contains(expectedPeriod),
                "switching to " + mode + " recalculates and displays the right time range, usage and percentage without Save & sync");
            CaptureSharedCard(switchingWindow, "period-" + mode + ".png");
            Check(switchItems.Items.Cast<AiDeviceUsage>().Single(item => !item.IsCurrentDevice).TotalTokens == 200 &&
                  handler.LastEndpoint!.Host == "sync.example.test",
                "policy switching retains other-device counts and pending connection edits");
        }
        ClickAndWait(switchClear);
        File.AppendAllText(switchingLog, SwitchingRecord(25, switchingNow));
        ((RadioButton)switchingWindow.FindName("SharedUsageWeeklyRadio")).IsChecked = true;
        WaitForSharedOperation(switchSave);
        Check(switchItems.Items.Cast<AiDeviceUsage>().Single(item => item.IsCurrentDevice).TotalTokens == 65 && !switchRestore.IsEnabled,
            "switching away from a manually cleared cumulative mode displays the full weekly usage");
        ((RadioButton)switchingWindow.FindName("SharedUsageNeverRadio")).IsChecked = true;
        WaitForSharedOperation(switchSave);
        Check(switchItems.Items.Cast<AiDeviceUsage>().Single(item => item.IsCurrentDevice).TotalTokens == 25 && switchRestore.IsEnabled,
            "returning to cumulative mode retains its own clear and undo baseline and new usage");
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
        var unknownSettingsPath = Path.Combine(switchingDirectory, "unknown-settings.json");
        var unknownSettings = new AiManagerSettings
        {
            SharedUsageEnabled = true, SharedUsageDeviceName = "Test PC", SharedUsageServerUrl = "https://sync.example.test",
            SharedUsageSyncKeyProtected = LocalSecretProtection.Protect(new string('x', 32))
        };
        File.WriteAllText(unknownSettingsPath, JsonSerializer.Serialize(unknownSettings));
        var unknownHandler = new SharedUsageSyncTestHandler();
        var unknownService = new AiManagerService(unknownSettingsPath, switchingSource, new SharedUsageClient(unknownHandler),
            _ => Task.FromResult<System.Collections.Generic.IReadOnlyList<AiLimitWindow>>([]));
        var unknownWindow = new AiManagerWindow(unknownService);
        unknownWindow.LoadSharedUsageControls();
        ((RadioButton)unknownWindow.FindName("SharedUsageAccountResetRadio")).IsChecked = true;
        WaitForSharedOperation((Button)unknownWindow.FindName("SharedUsageSaveButton"));
        Check(unknownHandler.RequestCount == 0 &&
              ((TextBlock)unknownWindow.FindName("SharedUsageStatusText")).Text.Contains("Last reset time is unknown"),
            "unknown official reset time shows a clear message and never uploads a false zero or lifetime count");
        unknownWindow.Close();
        app.ExitApplication();
        Console.WriteLine("Shared usage test artifacts: " + root);
    }
}
