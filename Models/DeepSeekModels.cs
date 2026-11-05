using System.Globalization;
using AIMaster.Services;

namespace AIMaster.Models;

/// <summary>Data sources AIMaster can display. Stored in settings as a stable identifier.</summary>
public static class AiProviderModes
{
    public const string Codex = "codex";
    public const string DeepSeek = "deepseek";

    public static string Normalize(string? value) =>
        string.Equals(value, DeepSeek, StringComparison.OrdinalIgnoreCase) ? DeepSeek : Codex;

    public static bool IsDeepSeek(string? value) => Normalize(value) == DeepSeek;
}

/// <summary>Account balance returned by the documented DeepSeek /user/balance endpoint.</summary>
public sealed class AiDeepSeekBalance
{
    public string Currency { get; init; } = "CNY";
    public double Total { get; init; }
    public double Granted { get; init; }
    public double ToppedUp { get; init; }
    public bool IsAvailable { get; init; }

    public string Symbol => Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "$" : "¥";
    public string TotalText => Symbol + Total.ToString("0.00", CultureInfo.InvariantCulture);
    public string PaidText => Symbol + ToppedUp.ToString("0.00", CultureInfo.InvariantCulture);
    public string GrantedText => Symbol + Granted.ToString("0.00", CultureInfo.InvariantCulture);
    public string CurrencyLabel => Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "USD" : "CNY";
}

/// <summary>One local day of DeepSeek platform usage. The platform reports cost and tokens per bucket.</summary>
public sealed class AiDeepSeekUsageDay
{
    public DateOnly Date { get; init; }
    public long Tokens { get; init; }
    public double Cost { get; init; }

    public AiDailyUsage ToTokenDay() => new() { Date = Date, Tokens = Tokens };
}

public sealed class AiDeepSeekModelUsage
{
    public string Model { get; init; } = string.Empty;
    public long Tokens { get; init; }
    public double Cost { get; init; }
}

/// <summary>Detailed usage read from DeepSeek Platform with a signed-in browser token.</summary>
public sealed class AiDeepSeekUsage
{
    public string Currency { get; init; } = "CNY";
    public long TodayTokens { get; init; }
    public double TodayCost { get; init; }
    public long PeriodTokens { get; init; }
    public double PeriodCost { get; init; }
    public int RequestCount { get; init; }
    public int ApiKeyCount { get; init; }
    public string? TopModel { get; init; }
    public bool IsMonthlyFallback { get; init; }
    public List<AiDeepSeekUsageDay> Daily { get; init; } = new();
    public List<AiDeepSeekModelUsage> Models { get; init; } = new();

    public string Symbol => Currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "$" : "¥";
    public string PeriodLabel => IsMonthlyFallback
        ? LocalizationService.L("本月", "This month")
        : LocalizationService.L("近 30 天", "Last 30 days");

    public string Money(double value) => Symbol + value.ToString("0.00##", CultureInfo.InvariantCulture);
}

/// <summary>One DeepSeek refresh. Balance and usage fail independently so either can still be shown.</summary>
public sealed class AiDeepSeekSnapshot
{
    public AiDeepSeekBalance? Balance { get; init; }
    public AiDeepSeekUsage? Usage { get; init; }
    public string? Error { get; init; }
    public string? UsageError { get; init; }
    public bool HasAnyData => Balance is not null || Usage is not null;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
}
