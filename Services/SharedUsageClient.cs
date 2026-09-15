using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

internal sealed class SharedUsageClient : IDisposable
{
    internal const string SyncKeyHeader = "X-AIMaster-Sync-Key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    private sealed class UploadRequest
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public DateOnly PeriodStart { get; init; }
        public DateOnly PeriodEnd { get; init; }
        public long TotalTokens { get; init; }
        public int SessionCount { get; init; }
        public string MostUsedModel { get; init; } = "unknown";
        public DateTimeOffset CapturedAt { get; init; }
    }

    private sealed class SyncResponse
    {
        public bool Success { get; init; }
        public GroupResponse Group { get; init; } = new();
        public List<DeviceResponse> Devices { get; init; } = new();
        public string? Error { get; init; }
    }

    private sealed class GroupResponse
    {
        public int DeviceCount { get; init; }
        public long TotalTokens { get; init; }
    }

    private sealed class DeviceResponse
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public long TotalTokens { get; init; }
        public double TokenSharePercent { get; init; }
        public int SessionCount { get; init; }
        public string? MostUsedModel { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    public async Task<AiSharedUsageSummary> SyncAsync(
        AiManagerSettings settings,
        AiLocalUsageSummary localUsage,
        CancellationToken cancellationToken = default)
    {
        if (!settings.SharedUsageEnabled)
            return new AiSharedUsageSummary { Enabled = false };
        if (!TryBuildSyncEndpoint(settings.SharedUsageServerUrl, out var endpoint, out var urlError))
            return Error(urlError);
        if (settings.SharedUsageSyncKey.Trim().Length is < 32 or > 512)
            return Error(LocalizationService.L("同步密钥需要 32–512 个字符", "The sync key must contain 32–512 characters"));
        if (string.IsNullOrWhiteSpace(settings.SharedUsageDeviceName))
            return Error(LocalizationService.L("设备名称不能为空", "The device name is required"));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation(SyncKeyHeader, settings.SharedUsageSyncKey.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(
                SerializeUpload(settings, localUsage), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            SyncResponse? payload = null;
            try { payload = JsonSerializer.Deserialize<SyncResponse>(responseJson, JsonOptions); }
            catch (JsonException) { }
            if (!response.IsSuccessStatusCode || payload?.Success != true)
                return Error(payload?.Error ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var devices = payload.Devices
                .Select(item => new AiDeviceUsage
                {
                    DeviceId = item.DeviceId,
                    DeviceName = item.DeviceName,
                    TotalTokens = Math.Max(0, item.TotalTokens),
                    TokenSharePercent = Math.Clamp(item.TokenSharePercent, 0, 100),
                    SessionCount = Math.Max(0, item.SessionCount),
                    MostUsedModel = string.Equals(item.MostUsedModel, "unknown", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : item.MostUsedModel,
                    UpdatedAt = item.UpdatedAt,
                    IsCurrentDevice = string.Equals(item.DeviceId, settings.SharedUsageDeviceId,
                        StringComparison.OrdinalIgnoreCase)
                })
                .OrderByDescending(item => item.TotalTokens)
                .ThenBy(item => item.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return new AiSharedUsageSummary
            {
                Enabled = true,
                SyncedAt = DateTimeOffset.Now,
                Devices = devices,
                TotalTokens = Math.Max(0, payload.Group.TotalTokens)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(LocalizationService.L("连接服务器超时", "The server connection timed out"));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return Error(ex.Message);
        }
    }

    internal static string SerializeUpload(AiManagerSettings settings, AiLocalUsageSummary usage) =>
        JsonSerializer.Serialize(new UploadRequest
        {
            DeviceId = settings.SharedUsageDeviceId,
            DeviceName = settings.SharedUsageDeviceName.Trim(),
            PeriodStart = usage.PeriodStart,
            PeriodEnd = usage.PeriodEnd,
            TotalTokens = Math.Max(0, usage.TotalTokens),
            SessionCount = Math.Max(0, usage.SessionCount),
            MostUsedModel = string.IsNullOrWhiteSpace(usage.MostUsedModel) ? "unknown" : usage.MostUsedModel,
            CapturedAt = DateTimeOffset.UtcNow
        }, JsonOptions);

    internal static bool TryBuildSyncEndpoint(string? serverUrl, out Uri? endpoint, out string error)
    {
        endpoint = null;
        error = string.Empty;
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            error = LocalizationService.L("服务器地址无效", "The server URL is invalid");
            return false;
        }
        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
        {
            error = LocalizationService.L("公网同步必须使用 HTTPS", "Public sync servers must use HTTPS");
            return false;
        }
        endpoint = new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}/api/v1/aimaster/sync", UriKind.Absolute);
        return true;
    }

    private static AiSharedUsageSummary Error(string message) => new()
    {
        Enabled = true,
        Error = message
    };

    public void Dispose() => _httpClient.Dispose();
}
