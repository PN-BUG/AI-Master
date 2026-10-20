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
    private readonly ResetNotificationHistory _resetNotificationHistory;
    private bool _resetCatchUpPending;
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
        ReplayMissedResetNotification();
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
                SendResetNotification(latest.KindText,
                    latest.SummaryText + L(" · 社区转述，单击打开面板查看原文；到账以账号数据为准。",
                        " · Community report. Click to open the dashboard for sources; account data confirms recovery."));
            TryReplayMissedResetNotification();
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
        var announcements = data?.AlertEvents.OrderByDescending(item => item.AnnouncedAt).ToArray()
            ?? Array.Empty<CodexResetEvent>();
        ResetSignalItems.ItemsSource = announcements.Take(1).ToArray();
        ResetHistoryItems.ItemsSource = announcements.Skip(1).ToArray();
        ResetHistoryExpander.Visibility = announcements.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        ResetHistoryExpander.Header = L($"历史公告（{Math.Max(0, announcements.Length - 1)}）",
            $"Earlier announcements ({Math.Max(0, announcements.Length - 1)})");
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
        if (recovered.Count == 0)
        {
            TryReplayMissedResetNotification();
            return;
        }
        _lastAccountRecovery = snapshot.CapturedAt;
        RenderResetSignals();
        if (!_service.Settings.ResetMonitoringEnabled || !_service.Settings.ResetNotificationsEnabled) return;
        var limit = recovered[0];
        SendResetNotification(L("账号额度已恢复", "Account quota recovered"),
            L($"官方账号数据：{limit.DisplayName} · {limit.WindowName}，剩余 {limit.RemainingPercent:0.#}%。单击查看详情。",
                $"Official account data: {limit.DisplayName} · {limit.WindowName}, {limit.RemainingPercent:0.#}% remaining. Click for details."));
    }

    internal void ReplayMissedResetNotification()
    {
        _resetCatchUpPending = true;
        TryReplayMissedResetNotification();
    }

    private void TryReplayMissedResetNotification()
    {
        if (!_resetCatchUpPending || !_resetControlsLoaded || _lifetime.IsCancellationRequested ||
            !_service.Settings.ResetMonitoringEnabled || !_service.Settings.ResetNotificationsEnabled) return;
        var now = DateTimeOffset.Now;
        if (_resetNotificationHistory.WasSentToday(now))
        {
            _resetCatchUpPending = false;
            return;
        }
        if (_resetNotificationHistory.CanCatchUp(_lastAccountRecovery, now))
        {
            if (SendResetNotification(L("账号额度已恢复", "Account quota recovered"),
                    L($"今日 {_lastAccountRecovery!.Value.LocalDateTime:HH:mm} 已检测到账号额度恢复。补发今日重置提醒，单击查看详情。",
                        $"Account quota recovery was detected today at {_lastAccountRecovery!.Value.LocalDateTime:HH:mm}. Missed reset reminder. Click for details.")))
                _resetCatchUpPending = false;
            return;
        }
        // Only a fresh confirmed announcement can serve as a community catch-up reminder.
        if (_resetSnapshot is not { IsStale: false, Error: null } snapshot) return;
        var announcement = snapshot.Events.Where(item => item.Kind == "announcement" &&
                _resetNotificationHistory.CanCatchUp(item.AnnouncedAt, now))
            .OrderByDescending(item => item.AnnouncedAt).FirstOrDefault();
        if (announcement is not null && SendResetNotification(announcement.KindText,
                announcement.SummaryText + L(" · 补发今日提醒；社区转述，到账以账号数据为准。",
                    " · Missed reminder for today. Community report; account data confirms recovery.")))
            _resetCatchUpPending = false;
    }

    private bool SendResetNotification(string title, string message)
    {
        try
        {
            return _resetNotificationHistory.TrySend(
                () => App.Current.ShowResetNotification(title, message), DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetFooterStatus(L("重置通知发送记录保存失败：", "Unable to save reset notification history: ") + ex.Message,
                warning: true);
            return false;
        }
    }

    private void ResetSource_Click(object sender, RoutedEventArgs e)
    {
        var url = sender is Button { Tag: string source } ? CodexResetClient.SafeSourceUrl(source) : null;
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SetFooterStatus(L("无法打开链接：", "Unable to open link: ") + ex.Message, warning: true); }
    }
}
