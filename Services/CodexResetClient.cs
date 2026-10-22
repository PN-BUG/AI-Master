using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

/// <summary>Public community data only. No Codex authentication is sent to this service.</summary>
internal sealed class CodexResetClient : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextPoll;
    private CodexResetSnapshot? _last;

    public CodexResetClient(HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CodexResetSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock();
            if (now < _nextPoll && _last is not null) return _last;
            _nextPoll = now + PollInterval;
            try
            {
                // Two distinct endpoints, each requested no more than once per two minutes.
                var feedTask = ReadDocumentAsync("feed", cancellationToken);
                var forecastTask = ReadDocumentAsync("forecast", cancellationToken);
                var payloads = await Task.WhenAll(feedTask, forecastTask);
                using var feed = JsonDocument.Parse(payloads[0]);
                using var forecast = JsonDocument.Parse(payloads[1]);
                _last = Parse(feed.RootElement, forecast.RootElement, now);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                _last = _last is null
                    ? new CodexResetSnapshot([], null, null, now, now, true, ex.Message)
                    : _last with { CheckedAt = now, IsStale = true, Error = ex.Message };
            }
            return _last;
        }
        finally { _gate.Release(); }
    }

    private async Task<string> ReadDocumentAsync(string route, CancellationToken cancellationToken)
    {
        // The localized response keeps the English summary alongside localized_summary.
        // Cache both languages so switching the UI does not wait for another poll.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://codex-reset.com/api/{route}?locale=zh");
        request.Headers.UserAgent.ParseAdd("AIMaster/1.3.2 (+https://github.com/PN-BUG/AI-Master)");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            var retryAt = retry?.Date ?? (_clock() + (retry?.Delta ?? PollInterval));
            lock (_gate) { if (retryAt > _nextPoll) _nextPoll = retryAt; }
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    internal static CodexResetSnapshot Parse(JsonElement feed, JsonElement forecast, DateTimeOffset now)
    {
        if (feed.ValueKind != JsonValueKind.Object || forecast.ValueKind != JsonValueKind.Object ||
            !feed.TryGetProperty("events", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new JsonException("Unexpected Codex Reset response.");
        var fetchedAt = Date(feed, "fetched_at");
        var updatedAt = Date(forecast, "updated_at");
        var stale = fetchedAt is null || updatedAt is null ||
                    now - fetchedAt > TimeSpan.FromMinutes(10) || now - updatedAt > TimeSpan.FromMinutes(10) ||
                    fetchedAt > now + TimeSpan.FromMinutes(5) || updatedAt > now + TimeSpan.FromMinutes(5) ||
                    (feed.TryGetProperty("stale", out var flag) && flag.ValueKind == JsonValueKind.True);
        var lastResetAt = Date(forecast, "last_reset_at");
        var events = new List<CodexResetEvent>();
        foreach (var row in rows.EnumerateArray())
        {
            // Use only the published contract. Classifier guesses and banked resets are not resets.
            if (Text(row, "group") != "reset" || Text(row, "announcement_state") != "announced") continue;
            var item = Event(row, "announcement", "id", "announced_at");
            if (item is not null && item.AnnouncedAt <= now + TimeSpan.FromMinutes(5)) events.Add(item);
        }

        var signals = new List<CodexResetEvent>();
        if (forecast.TryGetProperty("official_signal", out var official) && official.ValueKind == JsonValueKind.Object)
        {
            var item = Event(official, "signal", "tweet_id", "at") ?? Event(official, "signal", "id", "announced_at");
            if (item is not null) signals.Add(item);
        }
        if (feed.TryGetProperty("signal", out var headline) &&
            headline.ValueKind == JsonValueKind.Object && headline.TryGetProperty("active", out var active) &&
            active.ValueKind == JsonValueKind.True)
        {
            if (Event(headline, "signal", "tweet_id", "at") is { } item) signals.Add(item);
        }
        // A forecast may still point to the parent post after the feed has its newer,
        // more specific reply. Source priority must not hide the latest valid preview.
        var signal = signals.Where(item => item.AnnouncedAt <= now + TimeSpan.FromMinutes(5) &&
                now - item.AnnouncedAt <= TimeSpan.FromHours(24) &&
                (lastResetAt is null || item.AnnouncedAt > lastResetAt))
            .OrderByDescending(item => item.AnnouncedAt).FirstOrDefault();

        return new CodexResetSnapshot(events.DistinctBy(item => item.Key)
            .OrderByDescending(item => item.AnnouncedAt).Take(20).ToList(), signal,
            lastResetAt, fetchedAt ?? now, now, stale);
    }

    private static CodexResetEvent? Event(JsonElement row, string kind, string idField, string timeField)
    {
        var id = Text(row, idField);
        var at = Date(row, timeField);
        var summary = Text(row, "summary");
        var url = SafeSourceUrl(Text(row, "url"));
        return string.IsNullOrWhiteSpace(id) || at is null || string.IsNullOrWhiteSpace(summary) || url is null
            ? null : new CodexResetEvent(id, kind, at.Value, summary, url, Text(row, "localized_summary"));
    }

    internal static string? SafeSourceUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host.Equals("x.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("codex-reset.com", StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri : null;

    private static string? Text(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Date(JsonElement row, string name) =>
        DateTimeOffset.TryParse(Text(row, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
