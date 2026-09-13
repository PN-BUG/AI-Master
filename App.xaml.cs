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

    public static new App Current => (App)WpfApplication.Current;
    public bool IsExiting { get; private set; }
    internal bool IsTrayIconVisible => _trayIcon?.Visible == true;

    protected override void OnStartup(StartupEventArgs e)
    {
        LocalizationService.Initialize(LoadSavedLanguage() ?? CultureInfo.CurrentUICulture.Name);
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
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        UpdateTrayLanguage();
    }

    public void ShowMainWindow()
    {
        if (MainWindow is null) return;
        if (!MainWindow.IsVisible) MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    public void NotifyRunningInBackground()
    {
        if (_backgroundNoticeShown || _trayIcon is null) return;
        _backgroundNoticeShown = true;
        _trayIcon.BalloonTipTitle = "AIMaster";
        _trayIcon.BalloonTipText = LocalizationService.L(
            "AIMaster 正在后台运行，单击托盘图标可重新打开。",
            "AIMaster is running in the background. Click the tray icon to reopen it.");
        _trayIcon.ShowBalloonTip(2500);
    }

    public void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;
        if (_trayIcon is not null) _trayIcon.Visible = false;
        foreach (Window window in Windows.Cast<Window>().ToList()) window.Close();
        Shutdown();
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
