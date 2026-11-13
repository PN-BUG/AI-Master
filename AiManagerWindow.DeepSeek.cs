using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AIMaster.Models;
using AIMaster.Services;

namespace AIMaster;

public partial class AiManagerWindow
{
    private void Provider_Checked(object sender, RoutedEventArgs e)
    {
        if (!_providerControlsReady || sender is not RadioButton { Tag: string tag }) return;
        var provider = AiProviderModes.Normalize(tag);
        if (string.Equals(provider, _service.Settings.Provider, StringComparison.OrdinalIgnoreCase)) return;
        // Read before writing: the file holds the other windows' credentials and preferences, so a
        // failed read must abort the switch instead of saving a defaults-only copy over them.
        if (!_service.TryReloadSettings())
        {
            SetFooterStatus(L("无法读取设置文件，数据源未切换。",
                "Unable to read the settings file; the data source was not changed."), warning: true);
            _providerControlsReady = false;
            (AiProviderModes.IsDeepSeek(_service.Settings.Provider) ? DeepSeekProviderButton : CodexProviderButton)
                .IsChecked = true;
            _providerControlsReady = true;
            return;
        }
        var settings = _service.Settings;
        settings.Provider = provider;
        try
        {
            _service.SaveSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetFooterStatus(L("无法切换数据源：", "Unable to switch data source: ") + ex.Message, warning: true);
            _providerControlsReady = false;
            (AiProviderModes.IsDeepSeek(_service.Settings.Provider) ? DeepSeekProviderButton : CodexProviderButton)
                .IsChecked = true;
            _providerControlsReady = true;
            return;
        }
        ApplyProviderMode(provider);
        // The task list is unified across data sources, so only the quota cards and the active
        // data source change; the shared feed keeps both sources' tasks visible.
        AiTaskSnapshotFeed.ActivateProvider(provider);
        _ = RefreshWhenIdleAsync();
    }

    private async Task RefreshWhenIdleAsync()
    {
        // A sync started for the previous provider may still be in flight. Let it finish, then
        // refresh again so the new provider is never left showing the old client's categories.
        while (_refreshing && !_lifetime.IsCancellationRequested)
            await Task.Delay(50);
        if (!_lifetime.IsCancellationRequested) await RefreshAsync();
    }

    private void ApplyProviderMode(string? provider)
    {
        _deepSeekMode = AiProviderModes.IsDeepSeek(provider);
        var codexVisibility = _deepSeekMode ? Visibility.Collapsed : Visibility.Visible;
        QuotaSummaryGrid.Visibility = codexVisibility;
        DeepSeekQuotaSummary.Visibility = _deepSeekMode ? Visibility.Visible : Visibility.Collapsed;
        GuardCard.Visibility = codexVisibility;
        ResetSignalsCard.Visibility = codexVisibility;
        ForecastCard.Visibility = codexVisibility;
        SharedUsageCard.Visibility = codexVisibility;
        CodexPolicyPanel.Visibility = codexVisibility;
        SavePolicyButton.Visibility = codexVisibility;
        DeepSeekPolicyNote.Visibility = _deepSeekMode ? Visibility.Visible : Visibility.Collapsed;
        DailyUsageChartPanel.Visibility = _deepSeekMode ? Visibility.Collapsed : Visibility.Visible;
        if (_deepSeekMode) LimitsCard.Visibility = Visibility.Collapsed;
        else if (IsLoaded) StartResetRefresh();

        if (_deepSeekMode)
        {
            RemainingCaptionText.Text = LocalizationService.T("可用余额");
            RemainingUnitText.Visibility = Visibility.Collapsed;
            MainUsageBar.Visibility = Visibility.Collapsed;
            RemainingText.FontSize = 38;
            if (_service.LastSnapshot is not { IsDeepSeek: true })
            {
                RemainingText.Text = "--";
                QuotaStateText.Text = LocalizationService.T("正在同步");
                QuotaStateText.Foreground = ThemeBrush("SignalText");
                QuotaStateBadge.Background = ThemeBrush("SignalSoft");
                MainUsedText.Text = LocalizationService.T("等待 DeepSeek 数据");
                MainResetText.Text = string.Empty;
            }
        }
        else
        {
            RemainingCaptionText.Text = LocalizationService.T("剩余额度");
            RemainingUnitText.Visibility = Visibility.Visible;
            MainUsageBar.Visibility = Visibility.Visible;
            RemainingText.FontSize = 48;
        }
        UpdateProviderTexts();
        UpdateProviderCardTitles();
        UpdateDashboardPageLanguage();
        RenderResetAttention();
    }

    internal void UpdateProviderCardTitles()
    {
        if (_dashboardCards.TryGetValue("quota", out var quota))
            quota.HeaderTitle.Text = LocalizationService.T(_deepSeekMode ? "账户余额" : "账户额度");
        if (_dashboardCards.TryGetValue("settings", out var settings))
            settings.HeaderTitle.Text = LocalizationService.T(_deepSeekMode ? "应用设置" : "保护策略");
        if (_dashboardCards.TryGetValue("dailyUsage", out var daily))
            daily.HeaderTitle.Text = LocalizationService.T(_deepSeekMode ? "当日消耗" : "当日消耗 / 时段");
    }

    internal void UpdateProviderTexts()
    {
        ThreadsHelpText.Text = _deepSeekMode
            ? LocalizationService.L(
                "任务列表统一显示 Codex 与 Deep Code 的任务，并标注来源与所用模型；无需 Deep Code 正在运行。",
                "Tasks from Codex and Deep Code appear in one list labeled by source and model; Deep Code does not need to be running.")
            : LocalizationService.L(
                "任务列表统一显示 Codex 与 Deep Code 的任务，并标注来源与所用模型；运行态只对当前 App Server 连接可见。",
                "Tasks from Codex and Deep Code appear in one list labeled by source and model; live state is visible only through the current App Server connection.");
        LocalUsageSourceHelpText.Text = _deepSeekMode
            ? LocalizationService.T("本机 Token 来自 Deep Code 会话索引；DeepSeek 平台不提供逐条会话账单。")
            : LocalizationService.T("本机 Token 来自本地会话日志；账户 Token 来自 Codex 用量历史。");
        DailyUsageHelpText.Text = _deepSeekMode
            ? LocalizationService.T("DeepSeek 平台未提供分时用量；此卡显示当日总量。")
            : LocalizationService.T("按账号本周 Token 历史与周窗口已用比例估算，不是官方逐日账单。");
    }

    private void LoadDeepSeekControls()
    {
        var settings = _service.Settings;
        DeepSeekApiKeyBox.Password = settings.DeepSeekApiKey;
        DeepSeekPlatformTokenBox.Password = settings.DeepSeekPlatformToken;
        DeepSeekBalanceWarningBox.Text = settings.DeepSeekBalanceWarning.ToString("0.##", CultureInfo.InvariantCulture);
        DeepSeekSettingsStatusText.Text = string.IsNullOrWhiteSpace(settings.DeepSeekApiKey)
            ? L("尚未配置 API Key", "No API key configured")
            : L("已保存 API Key", "API key saved");
    }

    private async void SaveDeepSeek_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadBalanceWarning(DeepSeekBalanceWarningBox.Text, out var warning))
        {
            DeepSeekSettingsStatusText.Text = L("余额预警线需为不小于 0 的数字。",
                "The low-balance warning must be a number greater than or equal to 0.");
            return;
        }

        var settings = _service.ReloadSettings();
        settings.DeepSeekApiKey = DeepSeekApiKeyBox.Password.Trim();
        settings.DeepSeekPlatformToken = DeepSeekPlatformTokenBox.Password.Trim();
        settings.DeepSeekBalanceWarning = warning;
        try
        {
            _service.SaveSettings(settings);
            var status = string.IsNullOrWhiteSpace(settings.DeepSeekApiKey)
                ? L("尚未配置 API Key", "No API key configured")
                : L("DeepSeek 配置已保存", "DeepSeek settings saved");
            DeepSeekSettingsStatusText.Text = status;
            SetFooterStatus(status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeepSeekSettingsStatusText.Text = L("无法保存：", "Unable to save: ") + ex.Message;
            SetFooterStatus(DeepSeekSettingsStatusText.Text, warning: true);
            return;
        }

        if (_deepSeekMode) await RefreshAsync();
    }

    private static bool TryReadBalanceWarning(string text, out double value)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            value = 0;
            return true;
        }
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value) && value is >= 0 and < 1_000_000_000;
    }

    private void OpenDeepSeekPlatform_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://platform.deepseek.com/api_keys") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetFooterStatus(L("无法打开 DeepSeek 平台：", "Unable to open DeepSeek Platform: ") + ex.Message,
                warning: true);
        }
    }

    private void RenderDeepSeekSnapshot(AiManagerSnapshot snapshot)
    {
        _deepSeekMode = true;
        var deepSeek = snapshot.DeepSeek;
        var balance = deepSeek?.Balance;
        var settings = _service.Settings;

        RemainingCaptionText.Text = LocalizationService.T("可用余额");
        RemainingUnitText.Visibility = Visibility.Collapsed;
        MainUsageBar.Visibility = Visibility.Collapsed;
        MainUsageBar.Value = 0;
        ResetCreditText.Visibility = Visibility.Collapsed;
        ResetCreditText.Text = string.Empty;
        RemainingText.FontSize = 38;

        var lowBalance = settings.DeepSeekBalanceWarning > 0 &&
                         balance is { } current && current.Total < settings.DeepSeekBalanceWarning;
        if (balance is { } value)
        {
            RemainingText.Text = value.TotalText;
            RemainingText.FontSize = value.TotalText.Length switch { > 11 => 24, > 9 => 30, _ => 38 };
            MainUsedText.Text = L(
                $"付费 {value.PaidText} · 赠送 {value.GrantedText}（{value.CurrencyLabel}）",
                $"Paid {value.PaidText} · Granted {value.GrantedText} ({value.CurrencyLabel})");
            if (value.Total <= 0 || !value.IsAvailable)
            {
                QuotaStateText.Text = LocalizationService.T("余额不足");
                QuotaStateText.Foreground = ThemeBrush("DangerText");
                QuotaStateBadge.Background = ThemeBrush("DangerSoft");
            }
            else if (lowBalance)
            {
                QuotaStateText.Text = LocalizationService.T("余额偏低");
                QuotaStateText.Foreground = ThemeBrush("WarningText");
                QuotaStateBadge.Background = ThemeBrush("WarningSoft");
            }
            else
            {
                QuotaStateText.Text = LocalizationService.T("余额可用");
                QuotaStateText.Foreground = ThemeBrush("SignalText");
                QuotaStateBadge.Background = ThemeBrush("SignalSoft");
            }

            MainResetText.Text = deepSeek?.Usage is { } usage
                ? BuildDeepSeekUsageText(usage)
                : deepSeek?.UsageError ?? string.Empty;
            QuotaStateBadge.ToolTip = deepSeek?.UsageError;
        }
        else
        {
            RemainingText.Text = "--";
            MainUsedText.Text = deepSeek?.Error ?? L("等待 DeepSeek 数据", "Waiting for DeepSeek data");
            MainResetText.Text = L("在“设置”页填写 API Key 后即可读取余额和用量；平台 Token 可选择填写。",
                "Add an API key in Settings to read the balance and usage; the Platform token is optional.");
            QuotaStateText.Text = string.IsNullOrWhiteSpace(settings.DeepSeekApiKey)
                ? LocalizationService.T("未配置")
                : LocalizationService.T("余额同步暂不可用");
            QuotaStateText.Foreground = ThemeBrush("WarningText");
            QuotaStateBadge.Background = ThemeBrush("WarningSoft");
            QuotaStateBadge.ToolTip = deepSeek?.Error;
        }

        LimitItems.ItemsSource = null;
        LimitsCard.Visibility = Visibility.Collapsed;
        ThreadList.ItemsSource = _taskSnapshot is not null && AiTaskSnapshotFeed.Latest is { } shared
            ? shared.Tasks.Take(12).ToList()
            : snapshot.Threads;

        RenderLocalUsage(snapshot.LocalUsage, snapshot.DailyUsage, null);
        if (deepSeek?.Usage is { } detailed)
        {
            DailyUsageTokensText.Text = FormatTokens(detailed.TodayTokens);
            DailyUsageShareText.Text = L(
                $"今日 {detailed.Money(detailed.TodayCost)} · {detailed.PeriodLabel} {detailed.Money(detailed.PeriodCost)}",
                $"Today {detailed.Money(detailed.TodayCost)} · {detailed.PeriodLabel} {detailed.Money(detailed.PeriodCost)}");
            DailyUsageShareText.Foreground = ThemeBrush("Signal");
        }
        else
        {
            DailyUsageTokensText.Text = FormatTokens(snapshot.LocalUsage.TodayTokens);
            DailyUsageShareText.Text = deepSeek?.UsageError ?? L("DeepSeek 平台用量不可用", "DeepSeek Platform usage unavailable");
            DailyUsageShareText.Foreground = ThemeBrush("Muted");
        }
        DailyUsageChartPanel.Visibility = Visibility.Collapsed;
        DailyUsageEmptyText.Visibility = Visibility.Collapsed;

        SetFooterStatus(balance is { } footerBalance
            ? L($"余额 {footerBalance.TotalText} · 数据来自 DeepSeek 官方接口。",
                $"Balance {footerBalance.TotalText} · Data from the official DeepSeek API.")
            : deepSeek?.Error ?? LocalizationService.T("等待 DeepSeek 数据"),
            warning: balance is null || balance.Total <= 0 || !balance.IsAvailable);
    }

    private static string BuildDeepSeekUsageText(AiDeepSeekUsage usage)
    {
        var money = usage.Money;
        return LocalizationService.IsEnglish
            ? $"Today {money(usage.TodayCost)} · {FormatTokens(usage.TodayTokens)} tokens; {usage.PeriodLabel} {money(usage.PeriodCost)} · {FormatTokens(usage.PeriodTokens)} tokens"
            : $"今日 {money(usage.TodayCost)} · {FormatTokens(usage.TodayTokens)} Token；{usage.PeriodLabel} {money(usage.PeriodCost)} · {FormatTokens(usage.PeriodTokens)} Token";
    }
}
