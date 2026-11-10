using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIMaster.Models;

namespace AIMaster;

public partial class AiManagerWindow
{
    private IReadOnlyList<AiLimitWindow> _displayedQuotaLimits = Array.Empty<AiLimitWindow>();

    private void QuotaWindowsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 560;
        QuotaSummaryGrid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 12);
        QuotaSummaryGrid.ColumnDefinitions[2].Width = stacked
            ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(WeeklyQuotaTile, stacked ? 0 : 2);
        Grid.SetRow(WeeklyQuotaTile, stacked ? 1 : 0);
        WeeklyQuotaTile.Margin = new Thickness(0, stacked ? 12 : 0, 0, 0);
    }

    private void RenderQuotaWindows(IReadOnlyList<AiLimitWindow> limits)
    {
        _displayedQuotaLimits = limits;
        // Keep account windows prominent; model-specific windows remain in the details card.
        var shortWindow = SelectQuotaWindow(limits, minutes => minutes == 5 * 60);
        var weeklyWindow = SelectQuotaWindow(limits, minutes => minutes is >= 6 * 24 * 60 and <= 8 * 24 * 60);
        ShortQuotaTile.Content = CreateQuotaWindowCard(L("5 小时额度", "5-hour quota"), shortWindow);
        WeeklyQuotaTile.Content = CreateQuotaWindowCard(L("周额度", "Weekly quota"), weeklyWindow);

        var additional = limits.Where(item => !ReferenceEquals(item, shortWindow) && !ReferenceEquals(item, weeklyWindow)).ToArray();
        LimitItems.ItemsSource = additional;
        LimitsCard.Visibility = additional.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static AiLimitWindow? SelectQuotaWindow(IEnumerable<AiLimitWindow> limits, Func<int, bool> matches) =>
        limits.Where(item => matches(item.WindowDurationMinutes))
            .OrderByDescending(item => item.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.UsedPercent)
            .FirstOrDefault();

    private QuotaWindowCard CreateQuotaWindowCard(string title, AiLimitWindow? window)
    {
        var tone = window is null ? "Idle"
            : window.UsedPercent >= _service.Settings.PausePercent ? "Danger"
            : window.UsedPercent >= _service.Settings.WarningPercent ? "Warning" : "Signal";
        var status = window is null ? L("暂无数据", "No data")
            : tone == "Danger" ? L("暂停线", "Pause limit")
            : tone == "Warning" ? L("接近限制", "Near limit") : L("额度安全", "Quota safe");
        return new QuotaWindowCard(
            title,
            window?.DisplayName ?? L("账户未返回此窗口", "Window not returned by account"),
            window?.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) ?? "--",
            window?.UsedPercent ?? 0,
            window?.UsedText ?? L("等待额度数据", "Waiting for quota data"),
            window?.ResetText ?? L("重置时间未知", "Reset time unknown"),
            status,
            ThemeBrush(tone + "Soft"),
            ThemeBrush(tone == "Idle" ? "Muted" : tone + "Text"),
            ThemeBrush(tone));
    }

    private sealed record QuotaWindowCard(
        string Title, string AccountName, string RemainingValue, double UsedPercent,
        string UsedText, string ResetText, string StatusText,
        Brush StatusBackground, Brush StatusForeground, Brush MeterBrush)
    {
        public string RemainingLabel => L("剩余额度", "Quota remaining");
    }
}
