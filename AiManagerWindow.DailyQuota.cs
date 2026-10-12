using System.Globalization;
using System.Windows;
using AIMaster.Models;
using AIMaster.Services;

namespace AIMaster;

public partial class AiManagerWindow
{
    private readonly DailyQuotaNotificationTracker _dailyQuotaNotifications = new();
    private DailyQuotaEstimate? _dailyQuotaEstimate;

    internal static bool TryReadDailyQuotaPercent(string text, out double value) =>
        double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        DailyQuotaPolicy.IsValidLimit(value);

    private void RenderDailyQuota(AiManagerSnapshot snapshot, bool notify = false)
    {
        _dailyQuotaEstimate = DailyQuotaPolicy.Estimate(snapshot);
        UseDailyRecommendationButton.IsEnabled = _dailyQuotaEstimate?.RecommendedPercent is not null;
        DailyRecommendationText.Text = _dailyQuotaEstimate?.RecommendedPercent is { } recommendation
            ? L($"推荐 {recommendation:0.#}%/天：本周期 {_dailyQuotaEstimate.SampleDays} 个完整使用日日均占比加 20% 余量，向上取整至 0.1%。",
                $"Recommended {recommendation:0.#}%/day: average of {_dailyQuotaEstimate.SampleDays} completed active day(s) in this cycle plus 20%, rounded up to 0.1%.")
            : L("完整用量数据不足，暂不能推荐；可手动填写。", "Not enough complete usage data to recommend a limit; enter one manually.");
        var settings = _service.Settings;
        var used = _dailyQuotaEstimate?.TodayPercent;
        var over = settings.DailyQuotaWarningEnabled && used >= settings.DailyQuotaLimitPercent;
        DailyQuotaStatusText.Text = used is { } percent
            ? L($"今日账号消耗约 {percent:0.##}% 周总额度" + (settings.DailyQuotaWarningEnabled ? $" · 每日预警线 {settings.DailyQuotaLimitPercent:0.#}%" : " · 每日预警未启用"),
                $"Today's account usage ≈ {percent:0.##}% of total weekly quota" + (settings.DailyQuotaWarningEnabled ? $" · Daily warning {settings.DailyQuotaLimitPercent:0.#}%" : " · Daily warning disabled"))
            : L("今日账号额度占比暂不可用，数据不足时不触发每日预警。", "Today's quota share is unavailable; daily alerts wait for sufficient data.");
        DailyQuotaStatusText.Foreground = ThemeBrush(over ? "WarningText" : "Subtle");
        if (notify && _dailyQuotaNotifications.Evaluate(used, DateOnly.FromDateTime(snapshot.CapturedAt.LocalDateTime), settings))
            App.Current.ShowDailyQuotaNotification(used!.Value, settings.DailyQuotaLimitPercent);
    }

    private void UseDailyRecommendation_Click(object sender, RoutedEventArgs e)
    {
        if (_dailyQuotaEstimate?.RecommendedPercent is not { } recommendation) return;
        DailyQuotaLimitBox.Text = recommendation.ToString("0.#", CultureInfo.InvariantCulture);
        DailyQuotaWarningCheck.IsChecked = true;
        SetFooterStatus(L("已填入推荐预警线，点击“保存并应用”后生效。", "Recommended warning filled in. Select Save & apply to enable it."));
    }
}
