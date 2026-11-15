using System.Globalization;
using AIMaster.Services;

namespace AIMaster.Models;

/// <summary>
/// One OpenCode Go allowance window. The plan reports how much of each window is already used;
/// the dashboard and the floating monitor show what is left so the plan reads like the Codex
/// quota windows, while the detailed wording keeps the used share in the tooltip.
/// </summary>
public sealed class AiGoQuotaWindow
{
    public string Id { get; init; } = string.Empty;
    public double UsedPercent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public string Status { get; init; } = "ok";

    public double RemainingPercent => Math.Max(0, 100 - Math.Clamp(UsedPercent, 0, 100));
    public bool IsExhausted => UsedPercent >= 100 ||
        !string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);

    public int WindowDurationMinutes => Id switch
    {
        "rolling" => 5 * 60,
        "weekly" => 7 * 24 * 60,
        _ => 30 * 24 * 60
    };

    /// <summary>Short label for the two-line quota band of the floating monitor.</summary>
    public string ShortLabel => Id switch
    {
        "rolling" => LocalizationService.L("套餐 5h", "Go 5h"),
        "weekly" => LocalizationService.L("套餐 周", "Go week"),
        _ => LocalizationService.L("套餐 月", "Go month")
    };

    /// <summary>Compact name used when several windows are listed in one line.</summary>
    public string CompactLabel => Id switch
    {
        "rolling" => LocalizationService.L("5h", "5h"),
        "weekly" => LocalizationService.L("周", "Week"),
        _ => LocalizationService.L("月", "Month")
    };

    public string WindowName => LocalizationService.FormatWindowDuration(WindowDurationMinutes);
    public string RemainingText => RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    public string UsedText => LocalizationService.L(
        $"{UsedPercent.ToString("0.#", CultureInfo.InvariantCulture)}% 已用",
        $"{UsedPercent.ToString("0.#", CultureInfo.InvariantCulture)}% used");
    public string ResetText => LocalizationService.FormatResetTime(ResetsAt);
}

/// <summary>
/// OpenCode Go allowance read from the plan's usage endpoint. A snapshot exists only when a Go
/// API key is configured, so its presence is what switches the DeepSeek account area from the
/// pay-as-you-go balance to the subscription quota; an error keeps the plan view and reports it.
/// </summary>
public sealed class AiGoQuotaSnapshot
{
    public AiGoQuotaWindow? Rolling { get; init; }
    public AiGoQuotaWindow? Weekly { get; init; }
    public AiGoQuotaWindow? Monthly { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;

    public bool HasAnyData => Rolling is not null || Weekly is not null || Monthly is not null;

    public IReadOnlyList<AiGoQuotaWindow> Windows => new[] { Rolling, Weekly, Monthly }
        .Where(window => window is not null)
        .Select(window => window!)
        .ToList();

    /// <summary>The window closest to its limit; that is the one worth showing as the headline.</summary>
    public AiGoQuotaWindow? Tightest => Windows
        .OrderBy(window => window.RemainingPercent)
        .ThenBy(window => window.WindowDurationMinutes)
        .FirstOrDefault();

    /// <summary>Next reset among the windows that report one.</summary>
    public DateTimeOffset? NextResetAt => Windows
        .Where(window => window.ResetsAt is not null)
        .Select(window => window.ResetsAt)
        .OrderBy(value => value)
        .FirstOrDefault();

    /// <summary>One line of every window's used share, e.g. <c>5h 4% · 周 42% · 月 21%</c>.</summary>
    public string UsedSummary => string.Join(" · ", Windows.Select(window =>
        $"{window.CompactLabel} {window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture)}%"));

    /// <summary>One line of every window's remaining share, e.g. <c>5h 96% · 周 58% · 月 79%</c>.</summary>
    public string RemainingSummary => string.Join(" · ", Windows.Select(window =>
        $"{window.CompactLabel} {window.RemainingText}"));
}
