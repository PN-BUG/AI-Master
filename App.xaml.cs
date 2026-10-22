using System.Globalization;
using System.Drawing;
using System.Text.Json;
using System.Windows;
using AIMaster.Services;
using Forms = System.Windows.Forms;

namespace AIMaster;

public partial class App : WpfApplication
{
    private Mutex? _instanceMutex;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayIconImage;
    private Forms.ToolStripMenuItem? _showMainMenuItem;
    private Forms.ToolStripMenuItem? _showFloatingMenuItem;
    private Forms.ToolStripMenuItem? _exitMenuItem;
    private bool _backgroundNoticeShown;
    private TrayNotificationQueue? _trayNotifications;
    private NotificationCenterService? _notificationCenter;
    private Exception? _notificationCenterStartupError;
    private readonly System.Windows.Threading.DispatcherTimer _trayNotificationTimer = new()
        { Interval = TimeSpan.FromSeconds(2) };

    public static new App Current => (App)WpfApplication.Current;
    public bool IsExiting { get; private set; }
    internal bool IsTrayIconVisible => _trayIcon?.Visible == true;

    protected override void OnStartup(StartupEventArgs e)
    {
        LocalizationService.Initialize(LoadSavedLanguage() ?? CultureInfo.CurrentUICulture.Name);
        if (!e.Args.Contains("--update-restart", StringComparer.Ordinal))
        {
            var processPath = Environment.ProcessPath;
            var updateLock = processPath is null ? null : Path.Combine(Path.GetDirectoryName(processPath)!, ".aimaster-update.lock");
            if (updateLock is not null && File.Exists(updateLock))
            {
                try { using var probe = File.Open(updateLock, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                catch (IOException)
                {
                    WpfMessageBox.Show(
                        LocalizationService.IsEnglish ? "AIMaster is being updated. Please wait for it to restart." : "AIMaster 正在更新，请等待程序自动重启。",
                        LocalizationService.IsEnglish ? "Update in progress" : "正在更新",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
            }
        }
        _instanceMutex = new Mutex(true, "AIMaster_Standalone_v1", out var createdNew);
        if (!createdNew)
        {
            WpfMessageBox.Show(
                LocalizationService.IsEnglish ? "AIMaster is already running." : "AIMaster 已在运行中。",
                LocalizationService.IsEnglish ? "Notice" : "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        InitializeTrayIcon();
        try
        {
            _notificationCenter = new NotificationCenterService(new WindowsNotificationCenter(
                action => Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.ApplicationIdle),
                ShowMainWindow));
            _notificationCenter.Failed += ReportNotificationFailure;
        }
        catch (Exception ex)
        {
            _notificationCenterStartupError = ex;
            Dispatcher.BeginInvoke(() => ReportNotificationFailure(ex), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    internal void InitializeTrayIcon()
    {
        if (_trayIcon is not null) return;
        var resource = GetResourceStream(
            new Uri("pack://application:,,,/AIMaster;component/Resources/AIMaster.ico"));
        if (resource is not null)
        {
            using var sourceIcon = new Icon(resource.Stream);
            _trayIconImage = (Icon)sourceIcon.Clone();
        }

        _showMainMenuItem = new Forms.ToolStripMenuItem();
        _showMainMenuItem.Click += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _showFloatingMenuItem = new Forms.ToolStripMenuItem();
        _showFloatingMenuItem.Click += (_, _) => Dispatcher.Invoke(AiFloatingWindow.ShowOrActivate);
        _exitMenuItem = new Forms.ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([
            _showMainMenuItem,
            _showFloatingMenuItem,
            new Forms.ToolStripSeparator(),
            _exitMenuItem
        ]);
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "AIMaster",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowMainWindow);
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayNotifications = new TrayNotificationQueue(notification =>
        {
            if (IsExiting || _trayIcon?.Visible != true) return false;
            _trayIcon.ShowBalloonTip(notification.Duration, notification.Title, notification.Message, notification.Icon);
            return true;
        });
        _trayIcon.BalloonTipShown += (_, _) => _trayNotifications.ConfirmShown();
        _trayIcon.BalloonTipClosed += (_, _) => _trayNotifications.Close();
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            _trayNotifications.ConfirmShown();
            _trayNotifications.Close();
            Dispatcher.Invoke(ShowMainWindow);
        };
        _trayNotificationTimer.Tick += (_, _) =>
        {
            _trayNotifications.Tick();
            try { _notificationCenter?.Tick(); }
            catch (Exception ex) { ReportNotificationFailure(ex); }
        };
        _trayNotificationTimer.Start();
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        UpdateTrayLanguage();
    }

    public void ShowMainWindow()
    {
        if (MainWindow is null) return;
        if (!MainWindow.IsVisible) MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
        if (MainWindow is AiManagerWindow manager) manager.ReplayMissedResetNotification();
    }

    public void NotifyRunningInBackground()
    {
        if (_backgroundNoticeShown || _trayIcon is null) return;
        _backgroundNoticeShown = true;
        var message = LocalizationService.L(
            "AIMaster 正在后台运行，单击托盘图标可重新打开。",
            "AIMaster is running in the background. Click the tray icon to reopen it.");
        _trayNotifications?.Enqueue(new("AIMaster", message, Forms.ToolTipIcon.Info, 2500));
    }

    internal void ShowQuotaNotification(QuotaNotification notification, double threshold)
    {
        if (IsExiting || _trayIcon?.Visible != true) return;
        var limit = notification.Limit;
        var title = notification.IsPauseLimit
            ? LocalizationService.L("AIMaster · 额度达到暂停线", "AIMaster · Quota pause limit reached")
            : LocalizationService.L("AIMaster · 额度预警", "AIMaster · Quota warning");
        var message = LocalizationService.L(
            $"{limit.DisplayName} · {limit.WindowName} 已用 {limit.UsedPercent:0.#}%（提醒线 {threshold:0.#}%）。{limit.ResetText}。单击查看详情。",
            $"{limit.DisplayName} · {limit.WindowName}: {limit.UsedPercent:0.#}% used (threshold {threshold:0.#}%). {limit.ResetText}. Click for details.");
        _trayNotifications?.Enqueue(new(title, message, Forms.ToolTipIcon.Warning, 5000));
    }

    internal void ShowDailyQuotaNotification(double used, double limit)
    {
        if (IsExiting || _trayIcon?.Visible != true) return;
        _trayNotifications?.Enqueue(new(
            LocalizationService.L("AIMaster · 每日限额预警", "AIMaster · Daily usage warning"),
            LocalizationService.L(
                $"今日账号消耗约 {used:0.##}% 周总额度，达到每日预警线 {limit:0.#}%。按账号 Token 比例估算，含所有设备，次日重新计数。单击查看详情。",
                $"Today's account usage is about {used:0.##}% of total weekly quota, reaching the daily warning of {limit:0.#}%. Estimated from account token shares across all devices; resets next local day. Click for details."),
            Forms.ToolTipIcon.Warning, 5000));
    }

    public void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;
        if (_trayIcon is not null) _trayIcon.Visible = false;
        foreach (Window window in Windows.Cast<Window>().ToList()) window.Close();
        Shutdown();
    }

    internal bool ShowResetNotification(string title, string message, string eventKey, Action retained)
    {
        if (IsExiting) return false;
        if (_notificationCenterStartupError is not null)
            throw new InvalidOperationException("Windows notification registration failed.", _notificationCenterStartupError);
        return _notificationCenter?.SendReset(title, message, eventKey, retained) == true;
    }

    private void ReportNotificationFailure(Exception error)
    {
        System.Diagnostics.Debug.WriteLine($"[NotificationCenter] {error}");
        if (MainWindow is AiManagerWindow manager) manager.ReportResetNotificationError(error);
    }

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e) => UpdateTrayLanguage();

    private void UpdateTrayLanguage()
    {
        if (_showMainMenuItem is null || _showFloatingMenuItem is null || _exitMenuItem is null) return;
        _showMainMenuItem.Text = LocalizationService.L("显示主窗口", "Show main window");
        _showFloatingMenuItem.Text = LocalizationService.L("AI 悬浮监控", "AI floating monitor");
        _exitMenuItem.Text = LocalizationService.L("退出", "Exit");
    }

    private static string? LoadSavedLanguage()
    {
        try
        {
            var path = AppPaths.GetSettingsFile();
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("language", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _trayNotificationTimer.Stop();
        _notificationCenter?.Dispose();
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
        }
        _trayIconImage?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
