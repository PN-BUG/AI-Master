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
            signal = new { tweet_id = "preview", summary = "Global reset later today", at = now.AddMinutes(-1), url = source, active },
            events = new[] {
                new { id = "123", group = "reset", announcement_state = state, summary = "A reset announcement", url = source, announced_at = now.AddHours(-1) },
                new { id = "banked", group = "credits", announcement_state = "announced", summary = "Banked reset grant", url = source, announced_at = now }
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

        var account = new AccountResetTracker();
        AiLimitWindow Limit(double used, DateTimeOffset reset) => new()
            { LimitId = "codex", UsedPercent = used, ResetsAt = reset, WindowDurationMinutes = 10080 };
        var oldReset = now.AddDays(3);
        Check(account.Observe([Limit(90, oldReset)], now).Count == 0 &&
              account.Observe([], now).Count == 0 &&
              account.Observe([Limit(5, oldReset)], now).Count == 0,
            "initial account data, missing data, and usage corrections do not claim a reset");
        account.Observe([Limit(90, oldReset)], now);
        var recovered = Limit(5, now.AddDays(7));
        Check(account.Observe([recovered], now).Count == 1 && account.Observe([recovered], now).Count == 0,
            "official quota recovery needs a large usage drop and a new future window, and alerts once");

        var clock = now;
        var handler = new ResetHttpHandler(Feed(), Forecast());
        using (var client = new CodexResetClient(new HttpClient(handler), () => clock))
        {
            var live = client.ReadAsync().GetAwaiter().GetResult();
            client.ReadAsync().GetAwaiter().GetResult();
            Check(!live.IsStale && handler.Requests == 2 && handler.NoCredentials && handler.HasUserAgent,
                "public polling caches both endpoints, identifies AIMaster, and sends no credentials");
            clock += TimeSpan.FromMinutes(2);
            handler.Status = HttpStatusCode.TooManyRequests;
            var failed = client.ReadAsync().GetAwaiter().GetResult();
            clock += TimeSpan.FromMinutes(3);
            client.ReadAsync().GetAwaiter().GetResult();
            Check(failed.IsStale && failed.Events.Count == 1 && handler.Requests == 4,
                "429 preserves the last display, suppresses alerts, and respects Retry-After");
        }

        var app = new App();
        app.InitializeComponent();
        LocalizationService.Initialize(LocalizationService.English);
        var main = new AiManagerWindow();
        VerifyDashboardLayout();
        main.RenderResetSignals(Parse(Feed(active: true)));
        Check(main.FindName("ResetSignalStatusText") is TextBlock { Text: "Active reset preview / signal" } &&
              main.FindName("ResetMonitoringCheck") is CheckBox { Content: "Monitor reset signals (every 2 minutes)" },
            "reset monitoring card and active-signal state render in English");
        var output = args.SkipWhile(arg => arg != "--output").Skip(1).FirstOrDefault();
        var card = (Border)main.FindName("ResetSignalsCard");
        foreach (var language in new[] { LocalizationService.English, LocalizationService.Chinese })
        {
            LocalizationService.Initialize(language);
            main.RenderResetSignals(Parse(Feed(active: true)));
            card.Measure(new Size(430, double.PositiveInfinity));
            card.Arrange(new Rect(new Point(), card.DesiredSize));
            card.UpdateLayout();
            var items = (ItemsControl)main.FindName("ResetSignalItems");
            var row = (FrameworkElement)items.ItemTemplate.LoadContent();
            row.DataContext = ((CodexResetEvent[])items.ItemsSource)[0];
            row.Measure(new Size(400, double.PositiveInfinity));
            row.Arrange(new Rect(new Point(), row.DesiredSize));
            row.UpdateLayout();
            Check(Descendants(row).OfType<Button>().Any(button => Equals(button.Content,
                    language == LocalizationService.English ? "View source" : "查看原文")),
                $"{language} event template offers a localized source action");
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                var image = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth),
                    (int)Math.Ceiling(card.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                image.Render(card);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(output, $"reset-card-{language}.png"));
                encoder.Save(stream);
            }
        }
        main.Close();
        app.ExitApplication();
        Console.WriteLine("Reset signal checks passed.");
    }

    private sealed class ResetHttpHandler(string feed, string forecast) : HttpMessageHandler
    {
        public int Requests;
        public bool NoCredentials = true;
        public bool HasUserAgent = true;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            NoCredentials &= request.Headers.Authorization is null && !request.Headers.Contains("Cookie");
            HasUserAgent &= request.Headers.UserAgent.ToString().Contains("AIMaster/");
            var response = new HttpResponseMessage(Status)
                { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("feed") ? feed : forecast) };
            if (Status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        }
    }
}
