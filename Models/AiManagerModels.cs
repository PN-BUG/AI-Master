using System.Text.Json.Serialization;
using AIMaster.Services;

namespace AIMaster.Models;

public static class TaskNameSources
{
    public const string ConversationTitle = "conversationTitle";
    public const string LatestUserMessage = "latestUserMessage";

    public static string Normalize(string? value) =>
        string.Equals(value, LatestUserMessage, StringComparison.OrdinalIgnoreCase)
            ? LatestUserMessage
            : ConversationTitle;
}

public static class FloatingFontScales
{
    public const double Default = 1.2;
    public const double Minimum = 1.0;
    public const double Maximum = 1.5;

    public static double Normalize(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : Default;
}

public sealed class AiManagerSettings
{
    [JsonPropertyName("warningPercent")]
    public double WarningPercent { get; set; } = 70;

    [JsonPropertyName("pausePercent")]
    public double PausePercent { get; set; } = 90;

    [JsonPropertyName("autoPause")]
    public bool AutoPause { get; set; } = true;

    [JsonPropertyName("refreshSeconds")]
    public int RefreshSeconds { get; set; } = 60;

    [JsonPropertyName("floatingRefreshSeconds")]
    public int FloatingRefreshSeconds { get; set; } = 2;

    [JsonPropertyName("floatingAutoCollapse")]
    public bool FloatingAutoCollapse { get; set; } = true;

    [JsonPropertyName("floatingFontScale")]
    public double FloatingFontScale { get; set; } = FloatingFontScales.Default;

    [JsonPropertyName("dashboardCardLayout")]
    public List<string> DashboardCardLayout { get; set; } = new();

    [JsonPropertyName("collapsedDashboardCards")]
    public List<string> CollapsedDashboardCards { get; set; } = new();

    [JsonPropertyName("dashboardCardSizes")]
    public Dictionary<string, AiDashboardCardSize> DashboardCardSizes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("taskNameSource")]
    public string TaskNameSource { get; set; } = TaskNameSources.ConversationTitle;

    [JsonPropertyName("language")]
    public string? Language { get; set; }
}

public sealed class AiDashboardCardSize
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public sealed class AiLimitWindow
{
    public string LimitId { get; init; } = string.Empty;
    public string Name { get; init; } = "Codex";
    public string DisplayName => LocalizationService.LocalizeKnownText(Name);
    public string WindowName => LocalizationService.FormatWindowDuration(WindowDurationMinutes);
    public double UsedPercent { get; init; }
    public double RemainingPercent => Math.Max(0, 100 - UsedPercent);
    public DateTimeOffset? ResetsAt { get; init; }
    public int WindowDurationMinutes { get; init; }
    public bool IsPrimary { get; init; }
    public string UsedText => LocalizationService.IsEnglish
        ? $"{UsedPercent:0.#}% used"
        : $"{UsedPercent:0.#}% 已用";
    public string RemainingText => LocalizationService.IsEnglish
        ? $"{RemainingPercent:0.#}% remaining"
        : $"剩余 {RemainingPercent:0.#}%";
    public string ResetText => LocalizationService.FormatResetTime(ResetsAt);
}

public sealed class AiDailyUsage
{
    public DateOnly Date { get; init; }
    public long Tokens { get; init; }
}

public sealed class AiProjectUsage
{
    public string ProjectName { get; init; } = string.Empty;
    public string ProjectPath { get; init; } = string.Empty;
    public long Tokens { get; init; }
    public double SharePercent { get; init; }
    public double? WeeklyQuotaPercent { get; init; }
    public int SessionCount { get; init; }
    public string TokensText => FormatTokens(Tokens);
    public string ShareText => $"{SharePercent:0.#}%";
    public double WeeklyQuotaBarValue => WeeklyQuotaPercent ?? 0;
    public string QuotaShareText => WeeklyQuotaPercent is { } quota
        ? LocalizationService.IsEnglish
            ? $"~{quota:0.##}% weekly quota · {SharePercent:0.#}% local"
            : $"约 {quota:0.##}% 周额度 · 本机占比 {SharePercent:0.#}%"
        : LocalizationService.L($"周额度 -- · 本机占比 {SharePercent:0.#}%",
            $"Weekly quota -- · {SharePercent:0.#}% local");

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000d:0.##}B",
        >= 1_000_000 => $"{tokens / 1_000_000d:0.##}M",
        >= 1_000 => $"{tokens / 1_000d:0.#}K",
        _ => $"{tokens}"
    };
}

public sealed class AiLocalUsageSummary
{
    public long TotalTokens { get; init; }
    public int SessionCount { get; init; }
    public List<AiProjectUsage> Projects { get; init; } = new();
}

public sealed class AiThreadSummary
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = "未命名任务";
    public string? ConversationTitle { get; init; }
    public string? LatestUserMessage { get; init; }
    public string DisplayTitle => LocalizationService.LocalizeKnownText(Title);
    public string Status { get; init; } = "notLoaded";
    public string? ModelName { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? RolloutPath { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public bool IsActive => Status is "active" or "inProgress" or "waitingOnApproval" or "waitingOnUserInput";
    public string StatusText => LocalizationService.FormatTaskStatus(Status);
    public string UpdatedText => LocalizationService.FormatUpdatedTime(UpdatedAt);
}

public sealed class AiManagerSnapshot
{
    public List<AiLimitWindow> Limits { get; init; } = new();
    public List<AiDailyUsage> DailyUsage { get; init; } = new();
    public List<AiThreadSummary> Threads { get; init; } = new();
    public long? LifetimeTokens { get; init; }
    public int ResetCredits { get; init; }
    public AiLocalUsageSummary LocalUsage { get; init; } = new();
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;

    public AiLimitWindow? MainLimit => Limits
        .Where(item => item.IsPrimary)
        .OrderByDescending(item => item.WindowDurationMinutes)
        .FirstOrDefault() ?? Limits.OrderByDescending(item => item.WindowDurationMinutes).FirstOrDefault();
}

public sealed record AiForecast(
    double DailyTokens,
    double DailyPercent,
    DateTimeOffset? ExhaustsAt,
    bool ExhaustsBeforeReset,
    string Summary);

public sealed record AiRemainingForecastPoint(
    DateOnly Date,
    double RemainingPercent,
    bool IsProjected);

public sealed class AiFloatingSnapshot
{
    public List<AiThreadSummary> Tasks { get; init; } = new();
    public string TaskName { get; init; } = "暂无最近任务";
    public string TaskStatus { get; init; } = "notLoaded";
    public string TaskStatusText { get; init; } = "等待任务";
    public string ModelName { get; init; } = "模型未知";
    public string? ReasoningEffort { get; init; }
    public double? RemainingPercent { get; init; }
    public string QuotaWindow { get; init; } = "额度窗口未知";
    public DateTimeOffset? ResetsAt { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
}
