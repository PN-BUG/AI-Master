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
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyResetSignals(string[] args)
    {
        if (args.Contains("--reset-live", StringComparer.OrdinalIgnoreCase))
        {
            using var publicClient = new CodexResetClient();
            var current = publicClient.ReadAsync().GetAwaiter().GetResult();
            Check(current.Error is null && !current.IsStale,
                $"live public API is fresh and parsed by the AIMaster client ({current.Events.Count} announcements)");
        }
        var now = DateTimeOffset.Parse("2026-10-11T12:00:00Z");
        var source = "https://x.com/thsottiaux/status/123";
        string Feed(string state = "announced", bool stale = false, bool active = false) => JsonSerializer.Serialize(new
        {
            fetched_at = now, stale,
            signal = new { tweet_id = "preview", summary = "Global reset later today", localized_summary = "今天晚些时候将进行全局重置", at = now.AddMinutes(-1), url = source, active },
            events = new[] {
                new { id = "123", group = "reset", announcement_state = state, summary = "A reset announcement", localized_summary = "一条重置公告", url = source, announced_at = now.AddHours(-1) },
                new { id = "banked", group = "credits", announcement_state = "announced", summary = "Banked reset grant", localized_summary = "发放备用重置", url = source, announced_at = now }
            }
        });
        string Forecast(DateTimeOffset? reset = null) => JsonSerializer.Serialize(new
            { updated_at = now, last_reset_at = reset ?? now.AddHours(-1), official_signal = (object?)null,
              probabilities = new { rounded_24h = 99 } });
        CodexResetSnapshot Parse(string feed, string? forecast = null)
        {
            using var a = JsonDocument.Parse(feed);
            using var b = JsonDocument.Parse(forecast ?? Forecast());
            return CodexResetClient.Parse(a.RootElement, b.RootElement, now);
        }
        var baseline = Parse(Feed());
        Check(!baseline.IsStale && baseline.Events.Count == 1 && baseline.Signal is null,
            "only published reset announcements count; forecast probability and banked grants do not alert");
        var previousLanguage = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.Initialize(LocalizationService.Chinese);
            Check(baseline.Events[0].SummaryText == "一条重置公告" &&
                  Parse(Feed(active: true)).Signal?.SummaryText == "今天晚些时候将进行全局重置",
                "announcement and preview bodies use the feed's Chinese translations");
            var officialForecast = JsonSerializer.Serialize(new
            {
                updated_at = now, last_reset_at = now.AddHours(-1),
                official_signal = new { tweet_id = "official", summary = "Reset coming soon",
                    localized_summary = "即将重置", at = now, url = source }
            });
            Check(Parse(Feed(), officialForecast).Signal?.SummaryText == "即将重置",
                "official forecast signals also retain their Chinese translation");
            Check((baseline.Events[0] with { ChineseSummary = " " }).SummaryText ==
                  "中文译文暂不可用，请查看原文。",
                "missing translations show an honest Chinese source prompt");
            LocalizationService.Initialize(LocalizationService.English);
            Check(baseline.Events[0].SummaryText == "A reset announcement" &&
                  Parse(Feed(active: true)).Signal?.SummaryText == "Global reset later today",
                "switching to English reuses the unchanged source summaries");
        }
        finally { LocalizationService.Initialize(previousLanguage); }
        Check(Parse(Feed("none")).Events.Count == 0,
            "unconfirmed classifier candidates do not become reset announcements");
        Check(Parse(Feed(active: true)).Signal?.Id == "preview" &&
              Parse(Feed(active: true), Forecast(now)).Signal is null,
            "active advance signals appear, then expire after the recorded reset");
        Check(Parse(Feed(stale: true)).IsStale &&
              Parse(Feed().Replace(now.ToString("yyyy-MM-ddTHH:mm:sszzz"), now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:sszzz"))).IsStale,
            "source stale flags and old fetch times suppress signal notifications");
        Check(CodexResetClient.SafeSourceUrl("file:///C:/Windows/notepad.exe") is null &&
              CodexResetClient.SafeSourceUrl("https://x.com.evil.example/") is null &&
              CodexResetClient.SafeSourceUrl("https://user@x.com/") is null &&
              CodexResetClient.SafeSourceUrl(source) == source,
            "source actions accept only HTTPS links on the expected source hosts");

        var directory = Path.Combine(Path.GetTempPath(), "aimaster-reset-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(directory, "state.json");
        try
        {
            var tracker = new CodexResetTracker(statePath);
            Check(tracker.Observe(baseline, true).Count == 0, "first fresh poll establishes a quiet baseline");
            var nextEvent = new CodexResetEvent("456", "announcement", now, "New announcement", source);
            var next = baseline with { Events = baseline.Events.Prepend(nextEvent).ToArray() };
            Check(tracker.Observe(next with { IsStale = true }, true).Count == 0 &&
                  tracker.Observe(next, true).Single().Id == "456",
                "stale data does not consume the next fresh notification");
            Check(new CodexResetTracker(statePath).Observe(next, true).Count == 0,
                "delivered announcements do not alert again after restart");
            var preview = next with { Signal = new CodexResetEvent("789", "signal", now, "Preview", source) };
            Check(tracker.Observe(preview, false).Count == 0 && tracker.Observe(preview, true).Count == 0,
                "muting consumes observed events without replaying them when unmuted");
            Check(tracker.Observe(preview with { Events = preview.Events.Append(
                    new CodexResetEvent("789", "announcement", now, "Now announced", source)).ToArray() }, true).Count == 1,
                "a preview can later deliver one separate announcement notification");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

        VerifyAccountResetRecovery();

        var clock = now;
        var handler = new ResetHttpHandler(Feed(), Forecast());
        using (var client = new CodexResetClient(new HttpClient(handler), () => clock))
        {
            var live = client.ReadAsync().GetAwaiter().GetResult();
            client.ReadAsync().GetAwaiter().GetResult();
            Check(!live.IsStale && handler.Requests == 2 && handler.NoCredentials && handler.HasUserAgent &&
                  handler.RequestsChinese,
                "public polling caches bilingual responses, identifies AIMaster, and sends no credentials");
            client.ReadAsync(forceRefresh: true).GetAwaiter().GetResult();
            Check(handler.Requests == 4, "manual refresh fetches new data inside the automatic polling interval");
            clock += TimeSpan.FromMinutes(2);
            handler.Status = HttpStatusCode.TooManyRequests;
            var failed = client.ReadAsync().GetAwaiter().GetResult();
            clock += TimeSpan.FromMinutes(3);
            client.ReadAsync(forceRefresh: true).GetAwaiter().GetResult();
            Check(failed.IsStale && failed.Events.Count == 1 && handler.Requests == 6,
                "429 preserves the last display, suppresses alerts, and even manual refresh respects Retry-After");
            clock += TimeSpan.FromMinutes(7);
            handler.Status = HttpStatusCode.OK;
            client.ReadAsync(forceRefresh: true).GetAwaiter().GetResult();
            Check(handler.Requests == 8, "manual refresh can fetch again after Retry-After expires");
        }

        VerifyResetHttpRecovery(Feed(), Forecast(), now);

        var app = new App();
        app.InitializeComponent();
        LocalizationService.Initialize(LocalizationService.English);
        var main = new AiManagerWindow();
        VerifyDashboardLayout();
        main.RenderResetSignals(baseline);
        Check(main.FindName("ResetSignalStatusText") is TextBlock { Text: "No active reset preview" } &&
              main.FindName("ResetSignalItems") is ItemsControl { Items.Count: 0, Visibility: Visibility.Collapsed } &&
              main.FindName("ResetHistoryItems") is ItemsControl { Items.Count: 1 } &&
              main.FindName("ResetHistoryExpander") is Expander
                  { Visibility: Visibility.Visible, IsExpanded: false, Header: "Past announcements (1)" },
            "without an active preview, even a single previous announcement stays in collapsed history");
        var activeSnapshot = Parse(Feed(active: true));
        main.RenderResetSignals(activeSnapshot with { Events = [baseline.Events[0] with { AnnouncedAt = now }] });
        Check(main.FindName("ResetSignalItems") is ItemsControl activeItems &&
              activeItems.Items.Cast<CodexResetEvent>().Single().Kind == "signal" &&
              main.FindName("ResetHistoryItems") is ItemsControl { Items.Count: 1 },
            "an active preview keeps the main slot even when an announcement has a newer publication time");
        main.RenderResetSignals(activeSnapshot);
        Check(main.FindName("ResetSignalStatusText") is TextBlock { Text: "Active reset preview / signal" } &&
              main.FindName("ResetMonitoringCheck") is CheckBox
                  { Content: TextBlock { Text: "Monitor reset signals (every 2 minutes)" } },
            "reset monitoring card and active-signal state render in English");
        var output = args.SkipWhile(arg => arg != "--output").Skip(1).FirstOrDefault();
        var card = (Border)main.FindName("ResetSignalsCard");
        foreach (var language in new[] { LocalizationService.English, LocalizationService.Chinese,
                     LocalizationService.English })
        {
            LocalizationService.SetLanguage(language);
            card.Measure(new Size(430, double.PositiveInfinity));
            card.Arrange(new Rect(new Point(), card.DesiredSize));
            card.UpdateLayout();
            var refresh = Descendants(card).OfType<Button>().Single(button => button.Name == "ResetRefreshButton");
            Check(Equals(refresh.Content, language == LocalizationService.English ? "↻ Refresh" : "↻ 刷新") &&
                  refresh.ActualWidth + refresh.Margin.Left + refresh.Margin.Right + 0.1 >= refresh.DesiredSize.Width,
                $"{language} reset refresh action is localized and fits in the card header");
            var items = (ItemsControl)main.FindName("ResetSignalItems");
            var row = (FrameworkElement)items.ItemTemplate.LoadContent();
            row.DataContext = ((CodexResetEvent[])items.ItemsSource)[0];
            row.Measure(new Size(400, double.PositiveInfinity));
            row.Arrange(new Rect(new Point(), row.DesiredSize));
            row.UpdateLayout();
            Check(Descendants(row).OfType<Button>().Any(button => Equals(button.Content,
                    language == LocalizationService.English ? "View source" : "查看原文")),
                $"{language} event template offers a localized source action");
            var expectedSummary = language == LocalizationService.English
                ? "Global reset later today" : "今天晚些时候将进行全局重置";
            Check(Descendants(row).OfType<TextBlock>().Any(text => text.Text == expectedSummary &&
                    Equals(text.ToolTip, expectedSummary)),
                $"{language} language switch updates cached announcement bodies and tooltips");
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                CaptureCard($"reset-card-{language}");
                main.RenderResetSignals(baseline);
                CaptureCard($"reset-card-history-only-{language}");
                main.RenderResetSignals(activeSnapshot);

                void CaptureCard(string name)
                {
                    card.Measure(new Size(430, double.PositiveInfinity));
                    card.Arrange(new Rect(new Point(), card.DesiredSize));
                    card.UpdateLayout();
                    var image = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth),
                        (int)Math.Ceiling(card.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    image.Render(card);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = File.Create(Path.Combine(output, name + ".png"));
                    encoder.Save(stream);
                }
            }
        }
        main.Close();
        app.ExitApplication();
        Console.WriteLine("Reset signal checks passed.");
    }

    private static void VerifyAccountResetRecovery()
    {
        VerifyResetNotificationCatchUp();
        var now = DateTimeOffset.Parse("2026-10-13T05:00:00Z");
        AiLimitWindow Limit(double used, DateTimeOffset reset, bool primary = true) => new()
            { LimitId = "codex", UsedPercent = used, ResetsAt = reset,
                WindowDurationMinutes = 10080, IsPrimary = primary };
        var account = new AccountResetTracker();
        var oldReset = now.AddDays(3);
        Check(account.Observe([Limit(90, oldReset)], now.AddMinutes(-1)).Count == 0 &&
              account.Observe([], now).Count == 0 &&
              account.Observe([Limit(5, oldReset)], now).Count == 0,
            "initial account data, missing data, and same-window usage corrections do not claim a reset");
        Check(account.Observe([Limit(2, now.AddDays(7))], now).Count == 1 &&
              account.Observe([Limit(2, now.AddDays(7))], now.AddMinutes(1)).Count == 0,
            "a new official period alerts once even when prior usage was below 20 percent");

        var delayed = new AccountResetTracker();
        delayed.Observe([Limit(90, now.AddHours(1))], now);
        Check(delayed.Observe([Limit(35, now.AddDays(7).AddHours(1), primary: false)], now.AddHours(3)).Count == 1,
            "delayed detection alerts with more than 10 percent already used and a changed window slot");
        Check(delayed.Observe([Limit(90, now.AddHours(1))], now.AddHours(4)).Count == 0 &&
              delayed.Observe([Limit(35, now.AddDays(7).AddHours(1))], now.AddHours(5)).Count == 0,
            "an older quota response cannot roll back the baseline and replay a reset");
        var future = new AccountResetTracker();
        future.Observe([Limit(80, oldReset)], now);
        Check(future.Observe([Limit(0, now.AddDays(8))], now.AddMinutes(1)).Count == 0,
            "a future period start cannot announce a reset before it occurs");

        var directory = Path.Combine(Path.GetTempPath(), "aimaster-account-reset-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.json");
        try
        {
            var persistent = new AccountResetTracker(path);
            persistent.Observe([Limit(5, oldReset)], now.AddMinutes(-1));
            persistent = new AccountResetTracker(path);
            Check(persistent.Observe([Limit(2, now.AddDays(7))], now).Count == 1,
                "the previous official period survives restart and catches recovery while closed");
            persistent = new AccountResetTracker(path);
            Check(persistent.LastRecoveryAt == now &&
                  persistent.Observe([Limit(2, now.AddDays(7))], now.AddMinutes(1)).Count == 0,
                "last recovery survives restart and delivered recovery is not replayed");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try { persistent.Observe([Limit(1, now.AddDays(8))], now.AddDays(1)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                Check(failed && persistent.LastRecoveryAt == now,
                    "failed persistence does not consume the next recovery in memory");
            }
            Check(persistent.Observe([Limit(1, now.AddDays(8))], now.AddDays(1)).Count == 1,
                "recovery can be retried after a state write failure");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        Console.WriteLine("Account reset recovery checks passed.");
    }

    private static void VerifyResetNotificationCatchUp()
    {
        VerifyNotificationCenter();
        VerifyTrayNotificationReceipts();
        var directory = Path.Combine(Path.GetTempPath(), "aimaster-reset-notices-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sent.json");
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test UTC+8", TimeSpan.FromHours(8), "Test UTC+8", "Test UTC+8");
        var now = DateTimeOffset.Parse("2026-10-20T16:00:00+08:00");
        var reset = now.AddHours(-2);
        var clock = now;
        try
        {
            var history = new ResetNotificationHistory(path, zone, () => clock);
            Check(history.CanCatchUp(reset, now) && !history.CanCatchUp(null, now) &&
                  !history.CanCatchUp(now.AddDays(-1), now) && !history.CanCatchUp(now.AddMinutes(1), now),
                "opening the app can replay a known reset today, but never an unknown, old, or future reset");
            Check(!history.TrySend(_ => false, now, "signal:test") && !File.Exists(path) &&
                  new ResetNotificationHistory(path, zone).CanCatchUp(reset, now),
                "system-suppressed or unavailable notifications remain eligible after restart");
            Action? confirm = null;
            Check(history.TrySend(receipt => { confirm = receipt; return true; }, now, "signal:test") &&
                  history.CanCatchUp(reset, now) && new ResetNotificationHistory(path, zone).CanCatchUp(reset, now),
                "an accepted request without Notification Center retention remains retryable after restart");
            confirm!();
            Check(!history.CanCatchUp(reset, now), "Notification Center retention consumes today's catch-up");
            history = new ResetNotificationHistory(path, zone, () => clock);
            Check(history.WasSentToday(now.AddHours(1)) && !history.CanCatchUp(reset, now.AddHours(1)) &&
                  !history.CanCatchUp(now.AddMinutes(30), now.AddHours(1)),
                "reopening and restarting do not replay today's notification through either reset source");
            var tomorrow = now.AddDays(1);
            Check(!history.CanCatchUp(reset, tomorrow) && history.CanCatchUp(tomorrow.AddHours(-1), tomorrow),
                "a new local day allows today's missed reset without replaying yesterday's reset");

            var boundary = now.Date.AddDays(1);
            var midnight = new DateTimeOffset(boundary, TimeSpan.FromHours(8));
            Check(history.CanCatchUp(midnight.ToUniversalTime(), midnight.AddMinutes(5)) &&
                  !history.CanCatchUp(midnight.AddSeconds(-1).ToUniversalTime(), midnight.AddMinutes(5)),
                "catch-up uses the local calendar day even when source timestamps are UTC");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                clock = tomorrow;
                try { history.TrySend(receipt => { receipt(); return true; }, tomorrow, "announcement:test"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                Check(failed && !history.CanCatchUp(tomorrow.AddHours(-1), tomorrow),
                    "a failed retention-history save retains the confirmed receipt in memory");
            }

            // Existing installations have recovery observations but no sent-history file.
            var accountPath = Path.Combine(directory, "account.json");
            File.WriteAllText(accountPath, JsonSerializer.Serialize(new AccountResetTracker.State
                { LastRecoveryAt = reset }));
            Check(new ResetNotificationHistory(Path.Combine(directory, "new-history.json"), zone)
                    .CanCatchUp(new AccountResetTracker(accountPath).LastRecoveryAt, now),
                "legacy recovery observations can produce one catch-up without deleting the detection baseline");
            var legacyPath = Path.Combine(directory, "legacy-sent.json");
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
                { LastSentAt = now, LastShownAt = now, LastRetainedAt = now }));
            var legacy = new ResetNotificationHistory(legacyPath, zone);
            var preview = new CodexResetEvent("2113162413008884099", "signal", reset,
                "Reset preview", "https://x.com/thsottiaux/status/2113162413008884099");
            var snapshot = new CodexResetSnapshot([], preview, now.AddDays(-2), now, now, false);
            Check(legacy.CanCatchUp(reset, now) && legacy.FindCatchUp(snapshot, now)?.Key == preview.Key,
                "legacy balloon and unregistered toast receipts do not prevent today's registered Notification Center catch-up");
            Check(legacy.FindCatchUp(snapshot with { IsStale = true }, now) is null &&
                  legacy.FindCatchUp(snapshot with { Error = "offline" }, now) is null &&
                  legacy.FindCatchUp(snapshot with { Signal = preview with { AnnouncedAt = now.AddDays(-1) } }, now) is null,
                "preview catch-up excludes stale, failed, and previous-day data");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        Console.WriteLine("Reset notification catch-up checks passed.");
    }

    private static void VerifyTrayNotificationReceipts()
    {
        var now = DateTimeOffset.Parse("2026-10-22T15:00:00+08:00");
        var displayed = new System.Collections.Generic.List<string>();
        var receipts = 0;
        var queue = new TrayNotificationQueue(item => { displayed.Add(item.Title); return true; }, () => now);
        var reset = new TrayNotification("reset", "Preview", System.Windows.Forms.ToolTipIcon.Info, 7000,
            "signal:test", () => receipts++);
        queue.Enqueue(new("quota", "Quota warning", System.Windows.Forms.ToolTipIcon.Warning, 5000));
        queue.Enqueue(reset);
        queue.Enqueue(reset);
        queue.ConfirmShown();
        Check(receipts == 0 && displayed.SequenceEqual(new[] { "quota" }),
            "a quota balloon receipt cannot confirm a queued reset and repeated opens do not duplicate pending resets");
        queue.Close();
        queue.ConfirmShown();
        queue.ConfirmShown();
        Check(receipts == 1 && displayed.SequenceEqual(new[] { "quota", "reset" }),
            "only the active reset's native display event confirms it, once");
        queue.Close();
        queue.Enqueue(reset with { Key = "signal:unconfirmed" });
        now = now.AddMinutes(3);
        queue.Tick();
        queue.ConfirmShown();
        Check(receipts == 1, "expiry without a display event never records delivery");
        Check(queue.Enqueue(reset with { Key = "signal:unconfirmed" }) && displayed.Count == 4,
            "an expired unconfirmed reset can be requested again on reopening");
    }

    private static void VerifyResetHttpRecovery(string feed, string forecast, DateTimeOffset now)
    {
        foreach (var failure in new[] { "transport", "eof", "timeout", "408", "500", "502", "503" })
        {
            var handler = new ResetHttpHandler(feed, forecast)
            {
                Response = (route, attempt, _) =>
                {
                    if (route != "feed" || attempt != 1) return Task.FromResult<HttpResponseMessage?>(null);
                    return failure switch
                    {
                        "transport" => throw new HttpRequestException("Connection interrupted"),
                        "eof" => throw new IOException("Unexpected EOF"),
                        "timeout" => throw new TaskCanceledException("Request timed out"),
                        _ => Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage((HttpStatusCode)int.Parse(failure)))
                    };
                }
            };
            using var client = new CodexResetClient(new HttpClient(handler), () => now);
            var recovered = client.ReadAsync().GetAwaiter().GetResult();
            Check(!recovered.IsStale && recovered.Error is null && recovered.Events.Single().Id == "123" &&
                  handler.FeedRequests == 2 && handler.ForecastRequests == 1,
                $"a transient reset {failure} failure recovers within the poll without marking good data stale");
        }

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            var handler = new ResetHttpHandler(feed, forecast) { Status = status };
            using var client = new CodexResetClient(new HttpClient(handler), () => now);
            var failed = client.ReadAsync().GetAwaiter().GetResult();
            Check(failed.IsStale && failed.Error is not null && handler.Requests == 2,
                $"reset HTTP {(int)status} does not trigger immediate retries");
        }

        var clock = now;
        var persistent = new ResetHttpHandler(feed, forecast);
        using (var client = new CodexResetClient(new HttpClient(persistent), () => clock))
        {
            var first = client.ReadAsync().GetAwaiter().GetResult();
            persistent.Response = (route, _, _) => route == "feed"
                ? throw new HttpRequestException("Still offline")
                : Task.FromResult<HttpResponseMessage?>(null);
            clock += TimeSpan.FromMinutes(2);
            var stale = client.ReadAsync().GetAwaiter().GetResult();
            Check(stale.IsStale && stale.Error is not null && stale.Events.SequenceEqual(first.Events) &&
                  stale.FetchedAt == first.FetchedAt && persistent.FeedRequests == 4 && persistent.ForecastRequests == 2,
                "exhausted reset retries stop after three attempts and preserve the last successful display");
            persistent.Response = null;
            var restored = client.ReadAsync(forceRefresh: true).GetAwaiter().GetResult();
            Check(!restored.IsStale && restored.Error is null && restored.Events.Count == 1,
                "the next successful manual refresh clears the reset connection failure");
        }

        clock = now;
        var busy = new ResetHttpHandler(feed, forecast)
        {
            Response = (_, _, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return Task.FromResult<HttpResponseMessage?>(response);
            }
        };
        using (var client = new CodexResetClient(new HttpClient(busy), () => clock))
        {
            var failed = client.ReadAsync().GetAwaiter().GetResult();
            clock += TimeSpan.FromMinutes(3);
            client.ReadAsync(forceRefresh: true).GetAwaiter().GetResult();
            Check(failed.IsStale && busy.Requests == 2,
                "a service Retry-After pauses immediate retries and manual refresh until the server permits requests");
        }

        var concurrentCooldown = new ResetHttpHandler(feed, forecast)
        {
            Response = async (route, _, token) =>
            {
                if (route == "feed") throw new HttpRequestException("Connection interrupted");
                await Task.Delay(50, token);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return response;
            }
        };
        using (var client = new CodexResetClient(new HttpClient(concurrentCooldown), () => now))
        {
            var failed = client.ReadAsync().GetAwaiter().GetResult();
            Check(failed.IsStale && concurrentCooldown.Requests == 2,
                "a Retry-After arriving from the other reset endpoint also stops retries already in backoff");
        }

        using var cancellation = new CancellationTokenSource();
        var canceled = new ResetHttpHandler(feed, forecast)
        {
            Response = (route, _, _) =>
            {
                if (route != "feed") return Task.FromResult<HttpResponseMessage?>(null);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                throw new HttpRequestException("Transient disconnect before cancellation");
            }
        };
        using (var client = new CodexResetClient(new HttpClient(canceled), () => now))
        {
            var propagated = false;
            try { client.ReadAsync(cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { propagated = true; }
            Check(propagated && canceled.FeedRequests == 1,
                "canceling during reset retry backoff aborts promptly and propagates cancellation to the caller");
        }
    }

    private sealed class ResetHttpHandler(string feed, string forecast) : HttpMessageHandler
    {
        public int Requests;
        public int FeedRequests;
        public int ForecastRequests;
        public bool NoCredentials = true;
        public bool HasUserAgent = true;
        public bool RequestsChinese = true;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Func<string, int, CancellationToken, Task<HttpResponseMessage?>>? Response;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            NoCredentials &= request.Headers.Authorization is null && !request.Headers.Contains("Cookie");
            HasUserAgent &= request.Headers.UserAgent.ToString().Contains("AIMaster/");
            RequestsChinese &= request.RequestUri!.Query == "?locale=zh";
            var route = request.RequestUri!.AbsolutePath.EndsWith("feed") ? "feed" : "forecast";
            var attempt = route == "feed" ? Interlocked.Increment(ref FeedRequests) : Interlocked.Increment(ref ForecastRequests);
            if (Response is not null && await Response(route, attempt, cancellationToken) is { } scripted) return scripted;
            var response = new HttpResponseMessage(Status)
                { Content = new StringContent(route == "feed" ? feed : forecast) };
            if (Status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        }
    }
}
