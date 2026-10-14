using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIMaster.Models;
using AIMaster.Services;

namespace AIMaster;

public partial class AiManagerWindow
{
    private readonly CodexResetClient _resetClient = new();
    private readonly DispatcherTimer _resetTimer = new() { Interval = CodexResetClient.PollInterval };
    private readonly CodexResetTracker _resetTracker = new(Path.Combine(
        Path.GetDirectoryName(AppPaths.GetSettingsFile())!, "reset-signal-state.json"));
    private readonly AccountResetTracker _accountResetTracker;
    private Task? _resetRefreshTask;
    private CodexResetSnapshot? _resetSnapshot;
    private bool _resetControlsLoaded;
    private DateTimeOffset? _lastAccountRecovery;
    private string? _resetStateError;

    private void InitializeResetMonitoring()
    {
        ResetMonitoringCheck.IsChecked = _service.Settings.ResetMonitoringEnabled;
        ResetNotificationsCheck.IsChecked = _service.Settings.ResetNotificationsEnabled;
        _resetControlsLoaded = true;
        _resetTimer.Tick += (_, _) => StartResetRefresh();
        _resetTimer.Start();
        StartResetRefresh();
    }

    private void StartResetRefresh()
    {
        if (_lifetime.IsCancellationRequested || _resetRefreshTask is { IsCompleted: false }) return;
        _resetRefreshTask = RefreshResetSignalsAsync();
    }

    private async Task RefreshResetSignalsAsync()
    {
        if (!_service.Settings.ResetMonitoringEnabled) { RenderResetSignals(); return; }
        try
        {
            var snapshot = await _resetClient.ReadAsync(_lifetime.Token);
            if (!_service.Settings.ResetMonitoringEnabled || _lifetime.IsCancellationRequested) return;
            _resetSnapshot = snapshot;
            _resetStateError = null;
            var alerts = _resetTracker.Observe(snapshot, _service.Settings.ResetNotificationsEnabled);
            // Coalesce a batch so reconnecting does not produce a burst of tray balloons.
            if (alerts.FirstOrDefault() is { } latest)
                App.Current.ShowResetNotification(latest.KindText,
                    latest.Summary + L(" · 社区转述，单击打开面板查看原文；到账以账号数据为准。",
                        " · Community report. Click to open the dashboard for sources; account data confirms recovery."));
            RenderResetSignals();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _resetStateError = ex.Message;
            RenderResetSignals();
        }
    }

    private void ResetSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!_resetControlsLoaded) return;
        var settings = _service.ReloadSettings();
        settings.ResetMonitoringEnabled = ResetMonitoringCheck.IsChecked == true;
        settings.ResetNotificationsEnabled = ResetNotificationsCheck.IsChecked == true;
        try
        {
            _service.SaveSettings(settings);
            SetFooterStatus(L("重置信号设置已保存", "Reset monitoring settings saved"));
            StartResetRefresh();
            RenderResetSignals();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetFooterStatus(L("设置保存失败：", "Unable to save settings: ") + ex.Message, warning: true);
        }
    }

    internal void RenderResetSignals(CodexResetSnapshot? snapshot = null)
    {
        if (snapshot is not null) _resetSnapshot = snapshot;
        var enabled = _service.Settings.ResetMonitoringEnabled;
        var data = _resetSnapshot;
        ResetSignalStatusText.Text = !enabled ? L("监测已关闭", "Monitoring disabled")
            : _resetStateError is not null ? L("提醒记录保存失败，通知已暂停", "Cannot save alert history; notifications paused")
            : data is { IsStale: true } ? L("数据过期或连接失败，暂停信号提醒", "Data stale or unavailable; signal alerts paused")
            : data?.Signal is not null ? L("有新的重置预告 / 信号", "Active reset preview / signal")
            : data is not null ? L("暂无有效重置预告", "No active reset preview")
            : L("等待重置信号", "Waiting for reset signals");
        ResetSignalStatusText.Foreground = ThemeBrush(data is { IsStale: true } || _resetStateError is not null
            ? "WarningText" : "Ink");
        ResetSignalStatusText.ToolTip = _resetStateError ?? data?.Error;
        ResetLastAnnouncementText.Text = data?.LastResetAt is { } at
            ? L($"网站记录的上次重置：{at.LocalDateTime:MM-dd HH:mm}",
                $"Last reset recorded by tracker: {at.LocalDateTime:MM-dd HH:mm}")
            : L("上次重置记录未知", "Last recorded reset unknown");
        ResetSignalItems.ItemsSource = data?.AlertEvents.OrderByDescending(item => item.AnnouncedAt)
            .Take(3).ToArray();
        ResetCheckedText.Text = data is null ? L("每 2 分钟自动检查", "Automatically checked every 2 minutes")
            : L($"检查于 {data.CheckedAt.LocalDateTime:HH:mm:ss} · 来源抓取于 {data.FetchedAt.LocalDateTime:HH:mm:ss}",
                $"Checked {data.CheckedAt.LocalDateTime:HH:mm:ss} · Source fetched {data.FetchedAt.LocalDateTime:HH:mm:ss}");
        ResetAccountRecoveryText.Text = _lastAccountRecovery is { } recovered
            ? L($"官方账号数据：{recovered.LocalDateTime:MM-dd HH:mm} 检测到额度恢复",
                $"Official account data: recovery detected {recovered.LocalDateTime:MM-dd HH:mm}")
            : L("官方账号数据：尚未检测到额度恢复", "Official account data: no recovery detected yet");
        LocalizationService.Apply(ResetSignalsCard);
    }

    private void ObserveAccountRecovery(AiManagerSnapshot snapshot)
    {
        IReadOnlyList<AiLimitWindow> recovered;
        try { recovered = _accountResetTracker.Observe(snapshot.Limits, snapshot.CapturedAt); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetFooterStatus(L("账号重置记录保存失败：", "Unable to save account reset history: ") + ex.Message, warning: true);
            return;
        }
        if (recovered.Count == 0) return;
        _lastAccountRecovery = snapshot.CapturedAt;
        RenderResetSignals();
        if (!_service.Settings.ResetMonitoringEnabled || !_service.Settings.ResetNotificationsEnabled) return;
        var limit = recovered[0];
        App.Current.ShowResetNotification(L("账号额度已恢复", "Account quota recovered"),
            L($"官方账号数据：{limit.DisplayName} · {limit.WindowName}，剩余 {limit.RemainingPercent:0.#}%。单击查看详情。",
                $"Official account data: {limit.DisplayName} · {limit.WindowName}, {limit.RemainingPercent:0.#}% remaining. Click for details."));
    }

    private void ResetSource_Click(object sender, RoutedEventArgs e)
    {
        var url = sender is Button { Tag: string source } ? CodexResetClient.SafeSourceUrl(source) : null;
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SetFooterStatus(L("无法打开链接：", "Unable to open link: ") + ex.Message, warning: true); }
    }
}
