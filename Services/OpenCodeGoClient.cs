using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

/// <summary>
/// Reads the OpenCode Go subscription allowance. The plan key is the one OpenCode uses for the
/// <c>opencode-go</c> provider; its usage endpoint reports the share of the rolling five-hour,
/// weekly and monthly windows already used, with the reset time of each window. No credential is
/// ever written to disk by this class.
/// </summary>
internal sealed class OpenCodeGoClient : IDisposable
{
    internal const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";
    internal const string ConsoleUrl = "https://opencode.ai/console";
    private static readonly string[] WindowIds = ["rolling", "weekly", "monthly"];

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _clock;

    public OpenCodeGoClient(HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<AiGoQuotaSnapshot> ReadAsync(string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var key = apiKey?.Trim() ?? string.Empty;
        var now = _clock();
        if (key.Length == 0)
            return new AiGoQuotaSnapshot
            {
                CapturedAt = now,
                Error = LocalizationService.L(
                    "尚未配置 OpenCode Go API Key。填写后这里显示套餐额度，而不是 DeepSeek 余额。",
                    "No OpenCode Go API key configured. Add one to show the plan allowance instead of the DeepSeek balance.")
            };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(UsageUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed(now, LocalizationService.L(
                    "OpenCode Go 接口响应超时，请稍后重试。",
                    "The OpenCode Go API timed out; try again later."));
            }
            catch (HttpRequestException ex)
            {
                return Failed(now, LocalizationService.L(
                    $"无法连接 OpenCode：{ex.Message}",
                    $"Unable to reach OpenCode: {ex.Message}"));
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return Failed(now, InvalidKeyError());
                if (!response.IsSuccessStatusCode)
                    return Failed(now, LocalizationService.L(
                        $"OpenCode Go 接口返回 HTTP {(int)response.StatusCode}。",
                        $"The OpenCode Go API returned HTTP {(int)response.StatusCode}."));

                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var snapshot = ParseUsage(document.RootElement);
                    return new AiGoQuotaSnapshot
                    {
                        Rolling = snapshot.Rolling,
                        Weekly = snapshot.Weekly,
                        Monthly = snapshot.Monthly,
                        Error = snapshot.HasAnyData ? null : UnreadableError(),
                        CapturedAt = now
                    };
                }
                catch (JsonException)
                {
                    return Failed(now, UnreadableError());
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private static AiGoQuotaSnapshot Failed(DateTimeOffset now, string error) =>
        new() { Error = error, CapturedAt = now };

    /// <summary>
    /// Reads the three allowance windows. Missing or malformed windows are simply omitted: the
    /// caller keeps whatever the endpoint did report, and an all-empty result becomes an error.
    /// </summary>
    internal static AiGoQuotaSnapshot ParseUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return new AiGoQuotaSnapshot();
        var usage = root;
        if (root.TryGetProperty("usage", out var nested) && nested.ValueKind == JsonValueKind.Object)
            usage = nested;

        return new AiGoQuotaSnapshot
        {
            Rolling = ReadWindow(usage, "rolling"),
            Weekly = ReadWindow(usage, "weekly"),
            Monthly = ReadWindow(usage, "monthly")
        };
    }

    private static AiGoQuotaWindow? ReadWindow(JsonElement usage, string id)
    {
        if (!TryGetProperty(usage, id, out var window) || window.ValueKind != JsonValueKind.Object)
            return null;
        var percent = ReadPercent(window);
        if (percent is null) return null;
        return new AiGoQuotaWindow
        {
            Id = id,
            UsedPercent = Math.Clamp(percent.Value, 0, 100),
            Status = ReadString(window, "status") ?? "ok",
            ResetsAt = ReadResetTime(window)
        };
    }

    internal static bool IsKnownWindow(string id) =>
        WindowIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    private static double? ReadPercent(JsonElement window)
    {
        if (!TryGetProperty(window, "percent", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            double.IsFinite(number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out number) && double.IsFinite(number)) return number;
        return null;
    }

    private static DateTimeOffset? ReadResetTime(JsonElement window)
    {
        if (!TryGetProperty(window, "resetsAt", out var value)) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var raw) || raw <= 0)
            return null;
        try
        {
            return raw > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                : DateTimeOffset.FromUnixTimeSeconds(raw);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string InvalidKeyError() => LocalizationService.L(
        "OpenCode Go API Key 无效或已过期，请在“设置”页重新填写。",
        "The OpenCode Go API key is invalid or expired. Update it in Settings.");

    private static string UnreadableError() => LocalizationService.L(
        "OpenCode 返回了无法解析的套餐额度。",
        "OpenCode returned unreadable plan allowance data.");

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
