using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AIMaster.Models;

namespace AIMaster.Services;

/// <summary>
/// Reads a DeepSeek account. The documented API key endpoint supplies the balance; the optional
/// Platform token additionally supplies the usage buckets shown on platform.deepseek.com.
/// No credential is ever written to disk by this class.
/// </summary>
internal sealed class DeepSeekClient : IDisposable
{
    internal const string ApiBaseUrl = "https://api.deepseek.com";
    internal const string PlatformBaseUrl = "https://platform.deepseek.com";
    private static readonly Uri BalanceUri = new($"{ApiBaseUrl}/user/balance");
    private static readonly Uri PlatformSummaryUri = new($"{PlatformBaseUrl}/api/v0/users/get_user_summary");
    private static readonly string[] DateKeys =
        ["date", "day", "dt", "stat_date", "statDate", "usage_date", "biz_date", "time", "timestamp"];
    private static readonly string[] TokenKeys =
        ["total_tokens", "tokens", "token_count", "usage_tokens", "amount", "total"];
    private static readonly string[] CostKeys =
        ["total_cost", "cost", "amount", "price", "charge", "fee", "money"];
    private static readonly string[] ModelKeys = ["model", "model_name", "modelName"];
    private static readonly string[] ApiKeyKeys = ["api_key_id", "api_key_name", "key_id", "key_name"];
    private static readonly string[] RequestKeys = ["requests", "request_count", "total_requests", "count"];

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _clock;

    public DeepSeekClient(HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = http is null;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public async Task<AiDeepSeekSnapshot> ReadAsync(string? apiKey, string? platformToken,
        CancellationToken cancellationToken = default)
    {
        var key = apiKey?.Trim() ?? string.Empty;
        var token = platformToken?.Trim() ?? string.Empty;
        var now = _clock();

        AiDeepSeekBalance? balance = null;
        string? balanceError = null;
        if (key.Length > 0)
        {
            try
            {
                using var document = await GetJsonAsync(BalanceUri, key, platform: false,
                    invalidCredential: CredentialError("API Key"), cancellationToken).ConfigureAwait(false);
                balance = ParseApiBalance(document.RootElement);
            }
            catch (Exception ex) when (ex is DeepSeekRequestException or JsonException)
            {
                balanceError = ex.Message;
            }
        }

        if (balance is null && token.Length > 0)
        {
            try
            {
                using var document = await GetJsonAsync(PlatformSummaryUri, token, platform: true,
                    invalidCredential: CredentialError("平台 Token"), cancellationToken).ConfigureAwait(false);
                balance = ParsePlatformBalance(document.RootElement);
            }
            catch (Exception ex) when (ex is DeepSeekRequestException or JsonException)
            {
                balanceError ??= ex.Message;
            }
        }

        if (key.Length == 0 && token.Length == 0)
            balanceError = LocalizationService.L(
                "尚未配置 DeepSeek。请在“设置”页填写 API Key（用于余额），平台 Token 可选（用于用量明细）。",
                "DeepSeek is not configured. Add an API key for the balance and, optionally, a Platform token for usage details in Settings.");

        AiDeepSeekUsage? usage = null;
        string? usageError = null;
        if (token.Length > 0)
        {
            try
            {
                usage = await ReadUsageAsync(token, balance?.Currency ?? "CNY", now, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DeepSeekRequestException or JsonException)
            {
                usageError = ex.Message;
            }
        }
        else if (key.Length > 0)
        {
            usageError = LocalizationService.L(
                "未填写平台 Token，只显示官方 API 余额；请在「设置 → DeepSeek 接入」填写平台 Token 后查看用量明细。",
                "No Platform token configured, so only the official API balance is shown; add the Platform token under Settings → DeepSeek access to view usage details.");
        }

        return new AiDeepSeekSnapshot
        {
            Balance = balance,
            Usage = usage,
            Error = balance is null ? balanceError ?? UnknownError() : null,
            UsageError = usageError,
            CapturedAt = now
        };
    }

    private async Task<AiDeepSeekUsage> ReadUsageAsync(string token, string currency,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var start = today.AddDays(-29);
        var end = today.AddDays(1);
        var offsetSeconds = (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalSeconds;

        try
        {
            using var amount = await GetJsonAsync(
                BuildUri("/api/v0/usage/by_api_key/amount", ("start", Unix(start)),
                    ("end", Unix(end)), ("tz", offsetSeconds.ToString(CultureInfo.InvariantCulture))),
                token, platform: true, invalidCredential: CredentialError("平台 Token"),
                cancellationToken).ConfigureAwait(false);
            using var cost = await GetJsonAsync(
                BuildUri("/api/v0/usage/by_api_key/cost", ("start", Unix(start)),
                    ("end", Unix(end)), ("tz", offsetSeconds.ToString(CultureInfo.InvariantCulture))),
                token, platform: true, invalidCredential: CredentialError("平台 Token"),
                cancellationToken).ConfigureAwait(false);
            return ParseUsage(amount.RootElement, cost.RootElement, now, start, today,
                monthlyFallback: false, currency);
        }
        catch (DeepSeekRequestException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-key endpoints are private and may change; fall back to the monthly totals.
            using var amount = await GetJsonAsync(
                BuildUri("/api/v0/usage/amount", ("month", now.Month.ToString(CultureInfo.InvariantCulture)),
                    ("year", now.Year.ToString(CultureInfo.InvariantCulture))),
                token, platform: true, invalidCredential: CredentialError("平台 Token"),
                cancellationToken).ConfigureAwait(false);
            using var cost = await GetJsonAsync(
                BuildUri("/api/v0/usage/cost", ("month", now.Month.ToString(CultureInfo.InvariantCulture)),
                    ("year", now.Year.ToString(CultureInfo.InvariantCulture))),
                token, platform: true, invalidCredential: CredentialError("平台 Token"),
                cancellationToken).ConfigureAwait(false);
            return ParseUsage(amount.RootElement, cost.RootElement, now,
                new DateOnly(now.Year, now.Month, 1), today, monthlyFallback: true, currency);
        }
    }

    private static Uri BuildUri(string path, params (string Name, string Value)[] query)
    {
        var builder = new UriBuilder(PlatformBaseUrl + path);
        builder.Query = string.Join('&', query.Select(item =>
            $"{Uri.EscapeDataString(item.Name)}={Uri.EscapeDataString(item.Value)}"));
        return builder.Uri;
    }

    private static string Unix(DateOnly date) =>
        new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now))
            .ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private async Task<JsonDocument> GetJsonAsync(Uri uri, string bearer, bool platform,
        string invalidCredential, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (platform) request.Headers.TryAddWithoutValidation("x-client-platform", "web");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeepSeekRequestException(LocalizationService.L(
                "DeepSeek 接口响应超时，请稍后重试。", "The DeepSeek API timed out; try again later."));
        }
        catch (HttpRequestException ex)
        {
            throw new DeepSeekRequestException(LocalizationService.L(
                $"无法连接 DeepSeek：{ex.Message}", $"Unable to reach DeepSeek: {ex.Message}"));
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new DeepSeekRequestException(invalidCredential);
            if (!response.IsSuccessStatusCode)
                throw new DeepSeekRequestException(LocalizationService.L(
                    $"DeepSeek 接口返回 HTTP {(int)response.StatusCode}。",
                    $"The DeepSeek API returned HTTP {(int)response.StatusCode}."));

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                throw new DeepSeekRequestException(LocalizationService.L(
                    "DeepSeek 返回了无法解析的数据。", "DeepSeek returned unreadable data."));
            }
        }
    }

    internal static AiDeepSeekBalance ParseApiBalance(JsonElement root)
    {
        var available = TryReadBool(root, "is_available") ?? false;
        var entries = new List<AiDeepSeekBalance>();
        if (TryReadProperty(root, "balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            foreach (var info in infos.EnumerateArray())
            {
                if (info.ValueKind != JsonValueKind.Object) continue;
                entries.Add(new AiDeepSeekBalance
                {
                    Currency = ReadString(info, "currency") ?? "CNY",
                    Total = ReadNumber(info, "total_balance") ?? 0,
                    Granted = ReadNumber(info, "granted_balance") ?? 0,
                    ToppedUp = ReadNumber(info, "topped_up_balance") ?? 0,
                    IsAvailable = available
                });
            }
        }

        if (entries.Count == 0)
            return new AiDeepSeekBalance { IsAvailable = false };
        var selected = entries.FirstOrDefault(item => IsUsd(item.Currency) && item.Total > 0)
                       ?? entries.FirstOrDefault(item => item.Total > 0)
                       ?? entries.FirstOrDefault(item => IsUsd(item.Currency))
                       ?? entries[0];
        return new AiDeepSeekBalance
        {
            Currency = selected.Currency,
            Total = selected.Total,
            Granted = selected.Granted,
            ToppedUp = selected.ToppedUp,
            IsAvailable = available || selected.Total > 0
        };
    }

    internal static AiDeepSeekBalance ParsePlatformBalance(JsonElement root)
    {
        var code = ReadInt(root, "code");
        if (code is not null && code != 0)
            throw new DeepSeekRequestException(code is 40002 or 40003
                ? CredentialError("平台 Token")
                : LocalizationService.L($"DeepSeek 平台返回错误码 {code}。",
                    $"The DeepSeek Platform returned code {code}."));

        JsonElement summary = root;
        if (TryReadProperty(root, "data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var bizCode = ReadInt(data, "biz_code");
            if (bizCode is not null && bizCode != 0)
                throw new DeepSeekRequestException(bizCode is 40002 or 40003
                    ? CredentialError("平台 Token")
                    : LocalizationService.L($"DeepSeek 平台返回错误码 {bizCode}。",
                        $"The DeepSeek Platform returned code {bizCode}."));
            if (TryReadProperty(data, "biz_data", out var bizData) && bizData.ValueKind == JsonValueKind.Object)
                summary = bizData;
        }

        var paid = SumWallets(summary, "normal_wallets");
        var granted = SumWallets(summary, "bonus_wallets");
        var currencies = paid.Keys.Union(granted.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        if (currencies.Count == 0)
            return new AiDeepSeekBalance { IsAvailable = false };

        var currency = currencies.FirstOrDefault(item =>
                           IsUsd(item) && paid.GetValueOrDefault(item) + granted.GetValueOrDefault(item) > 0)
                       ?? currencies.FirstOrDefault(item =>
                           paid.GetValueOrDefault(item) + granted.GetValueOrDefault(item) > 0)
                       ?? currencies.FirstOrDefault(IsUsd)
                       ?? currencies[0];
        var paidBalance = paid.GetValueOrDefault(currency);
        var grantedBalance = granted.GetValueOrDefault(currency);
        return new AiDeepSeekBalance
        {
            Currency = currency,
            ToppedUp = paidBalance,
            Granted = grantedBalance,
            Total = paidBalance + grantedBalance,
            IsAvailable = paidBalance + grantedBalance > 0
        };
    }

    internal static AiDeepSeekUsage ParseUsage(JsonElement amountRoot, JsonElement costRoot,
        DateTimeOffset now, DateOnly periodStart, DateOnly periodEnd, bool monthlyFallback,
        string currency = "CNY")
    {
        var amounts = new UsageAccumulator();
        Collect(amountRoot, isCost: false, periodStart, periodEnd, amounts, 0);
        var costs = new UsageAccumulator();
        Collect(costRoot, isCost: true, periodStart, periodEnd, costs, 0);

        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var days = new SortedDictionary<DateOnly, AiDeepSeekUsageDay>();
        foreach (var date in amounts.Days.Keys.Union(costs.Days.Keys))
        {
            days[date] = new AiDeepSeekUsageDay
            {
                Date = date,
                Tokens = amounts.Days.GetValueOrDefault(date).Tokens,
                Cost = costs.Days.GetValueOrDefault(date).Cost
            };
        }

        var hasDated = amounts.Days.Count > 0 || costs.Days.Count > 0;
        var periodTokens = hasDated ? amounts.Days.Values.Sum(item => item.Tokens) : amounts.UndatedTokens;
        var periodCost = hasDated ? costs.Days.Values.Sum(item => item.Cost) : costs.UndatedCost;
        var todayTokens = days.TryGetValue(today, out var todayDay) ? todayDay.Tokens : 0;
        var todayCost = days.TryGetValue(today, out var todayCostDay) ? todayCostDay.Cost : 0;

        var models = new Dictionary<string, AiDeepSeekModelUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, value) in amounts.Models)
            models[model] = new AiDeepSeekModelUsage { Model = model, Tokens = (long)value.Tokens };
        foreach (var (model, value) in costs.Models)
            models[model] = new AiDeepSeekModelUsage
            {
                Model = model,
                Tokens = models.TryGetValue(model, out var existing) ? existing.Tokens : 0,
                Cost = value.Cost
            };
        var modelList = models.Values
            .OrderByDescending(item => item.Cost).ThenByDescending(item => item.Tokens)
            .ToList();
        var topModel = modelList.FirstOrDefault(item => item.Cost > 0 || item.Tokens > 0)?.Model;

        return new AiDeepSeekUsage
        {
            Currency = currency,
            TodayTokens = todayTokens,
            TodayCost = todayCost,
            PeriodTokens = periodTokens,
            PeriodCost = periodCost,
            RequestCount = Math.Max(amounts.Requests, costs.Requests),
            ApiKeyCount = amounts.ApiKeys.Union(costs.ApiKeys, StringComparer.OrdinalIgnoreCase).Count(),
            TopModel = topModel,
            IsMonthlyFallback = monthlyFallback,
            Daily = days.Values.ToList(),
            Models = modelList
        };
    }

    private sealed class UsageAccumulator
    {
        public Dictionary<DateOnly, (long Tokens, double Cost)> Days { get; } = new();
        public Dictionary<string, (long Tokens, double Cost)> Models { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ApiKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long UndatedTokens { get; set; }
        public double UndatedCost { get; set; }
        public int Requests { get; set; }
        public int Nodes { get; set; }
    }

    private static void Collect(JsonElement node, bool isCost, DateOnly min, DateOnly max,
        UsageAccumulator accumulator, int depth)
    {
        if (depth > 64 || ++accumulator.Nodes > 50_000) return;
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                Collect(item, isCost, min, max, accumulator, depth + 1);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;

        var date = TryReadDate(node);
        var tokens = isCost ? null : TryReadTokens(node);
        var cost = isCost ? TryReadCost(node) : null;
        var inRange = date is not null && date >= min && date <= max;

        if (inRange)
        {
            var current = accumulator.Days.GetValueOrDefault(date!.Value);
            accumulator.Days[date.Value] = (current.Tokens + (long)(tokens ?? 0), current.Cost + (cost ?? 0));
        }
        else if (date is null)
        {
            accumulator.UndatedTokens += (long)(tokens ?? 0);
            accumulator.UndatedCost += cost ?? 0;
        }

        var model = ReadFirstString(node, ModelKeys);
        if (!string.IsNullOrWhiteSpace(model) && (tokens is not null || cost is not null))
        {
            var current = accumulator.Models.GetValueOrDefault(model);
            accumulator.Models[model] = (current.Tokens + (long)(tokens ?? 0), current.Cost + (cost ?? 0));
        }

        var apiKey = ReadFirstString(node, ApiKeyKeys);
        if (!string.IsNullOrWhiteSpace(apiKey)) accumulator.ApiKeys.Add(apiKey);

        if (!isCost && date is not null)
        {
            var requests = ReadFirstInt(node, RequestKeys);
            if (requests is > 0 and < 100_000_000) accumulator.Requests += requests.Value;
        }

        foreach (var property in node.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Collect(property.Value, isCost, min, max, accumulator, depth + 1);
    }

    private static DateOnly? TryReadDate(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!DateKeys.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt64(out var raw))
                return FromUnix(raw);
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var text = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Length <= 10 && DateOnly.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) return date;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
                return FromUnix(raw);
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp))
                return DateOnly.FromDateTime(timestamp.LocalDateTime);
        }
        return null;
    }

    private static DateOnly? FromUnix(long value)
    {
        if (value <= 0) return null;
        try
        {
            var timestamp = value > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            return DateOnly.FromDateTime(timestamp.LocalDateTime);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static double? TryReadTokens(JsonElement element)
    {
        var total = ReadFirstNumber(element, TokenKeys);
        if (total is not null) return total;
        var prompt = ReadFirstNumber(element, ["prompt_tokens", "input_tokens"]);
        var completion = ReadFirstNumber(element, ["completion_tokens", "output_tokens"]);
        return prompt is null && completion is null ? null : (prompt ?? 0) + (completion ?? 0);
    }

    private static double? TryReadCost(JsonElement element) => ReadFirstNumber(element, CostKeys);

    private static Dictionary<string, double> SumWallets(JsonElement element, string property)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!TryReadProperty(element, property, out var wallets) || wallets.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var wallet in wallets.EnumerateArray())
        {
            if (wallet.ValueKind != JsonValueKind.Object) continue;
            var currency = ReadString(wallet, "currency");
            var balance = ReadNumber(wallet, "balance");
            if (string.IsNullOrWhiteSpace(currency) || balance is null) continue;
            result[currency] = result.GetValueOrDefault(currency) + balance.Value;
        }
        return result;
    }

    private static bool IsUsd(string currency) =>
        string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase);

    private static string CredentialError(string kind) => LocalizationService.L(
        $"DeepSeek {kind} 无效或已过期，请在“设置”页重新填写。",
        $"The DeepSeek {kind} is invalid or expired. Update it in Settings.");

    private static string UnknownError() => LocalizationService.L(
        "无法读取 DeepSeek 账户数据。", "Unable to read DeepSeek account data.");

    private static bool TryReadProperty(JsonElement element, string name, out JsonElement value)
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
        TryReadProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? ReadFirstString(JsonElement element, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var value = ReadString(element, name);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static double? ReadNumber(JsonElement element, string name)
    {
        if (!TryReadProperty(element, name, out var value)) return null;
        return ToDouble(value);
    }

    private static double? ReadFirstNumber(JsonElement element, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var value = ReadNumber(element, name);
            if (value is not null) return value;
        }
        return null;
    }

    private static int? ReadFirstInt(JsonElement element, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (!TryReadProperty(element, name, out var value)) continue;
            var number = ToDouble(value);
            if (number is >= 0) return (int)Math.Min(number.Value, int.MaxValue);
        }
        return null;
    }

    private static int? ReadInt(JsonElement element, string name) =>
        ReadNumber(element, name) is { } value ? (int)value : null;

    private static bool? TryReadBool(JsonElement element, string name)
    {
        if (!TryReadProperty(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static double? ToDouble(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            double.IsFinite(number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
            double.IsFinite(number)) return number;
        return null;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

internal sealed class DeepSeekRequestException(string message) : Exception(message);
