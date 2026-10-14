using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AIMaster.Models;
using AIMaster.Services;
using SharedUpdates;

namespace AIMaster;

public partial class AiManagerWindow : Window
{
    private readonly AiManagerService _service;
    private readonly DispatcherTimer _timer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshing;
    private bool _localGuardPaused;
    private bool _overrideCurrentBreach;
    private bool _alertedForCurrentBreach;
    private readonly QuotaNotificationTracker _quotaNotifications = new();
    private bool _checkingForUpdates;
    private bool _showLocalForecast;
    private string _theme = ThemeModes.Dark;
    private UpdateCheckResult? _latestRelease;
    private UpdateCheckResult? _availableUpdate;
    private IReadOnlyList<AiRemainingForecastPoint> _weeklyRemainingForecast =
        Array.Empty<AiRemainingForecastPoint>();
    private IReadOnlyList<AiHourlyUsage> _todayHourlyUsage = Array.Empty<AiHourlyUsage>();
    private readonly Dictionary<string, DashboardCardState> _dashboardCards =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedDashboardCards =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DashboardPageState> _dashboardPages = new();
    private string _activeDashboardPage = "quota";
    private Point _cardDragStart;
    private Border? _draggedCard;
    private const double MinimumCardWidth = 280;
    private const double MinimumCardHeight = 88;
    private const double MaximumCardHeight = 1200;

    private static readonly string[] DefaultDashboardLayout =
    [
        "left:quota", "left:limits", "left:threads", "left:localUsage", "left:dailyUsage",
        "right:guard", "right:resetSignals", "right:forecast", "left:settings", "right:sharedUsage"
    ];

    private sealed class DashboardPageState(
        Grid host, StackPanel leftColumn, StackPanel rightColumn, string description)
    {
        public Grid Host { get; } = host;
        public StackPanel LeftColumn { get; } = leftColumn;
        public StackPanel RightColumn { get; } = rightColumn;
        public string Description { get; } = description;
        public double ScrollOffset { get; set; }
    }

    private sealed record DashboardCardState(
        string Id,
        string Title,
        Border Card,
        UIElement Body,
        TextBlock HeaderTitle,
        Button HelpButton,
        TextBlock InteractionHelp,
        Button ToggleButton,
        Thumb ResizeThumb,
        double ExpandedMinHeight);

    public AiManagerWindow() : this(new AiManagerService()) { }

    internal AiManagerWindow(AiManagerService service)
    {
        _service = service;
        _accountResetTracker = new AccountResetTracker(service.AccountResetStatePath);
        _lastAccountRecovery = _accountResetTracker.LastRecoveryAt;
        InitializeComponent();
        ApplyTheme(_service.Settings.Theme, refreshContent: false);
        InitializeDashboardCards();
        _timer.Tick += async (_, _) => await RefreshAsync();
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        LocalizationService.Apply(this);
        UpdateDashboardChromeLanguage();
        UpdateLanguageButton();
        UpdateThemeButton();
        UpdateDashboardPageLanguage();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadPolicyControls();
        LoadSharedUsageControls();
        InitializeResetMonitoring();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(_service.Settings.RefreshSeconds, 30, 600));
        _timer.Start();
        _ = CheckForUpdatesAsync(showUpToDate: false);
        await RefreshAsync();
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _resetTimer.Stop();
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        _lifetime.Cancel();
        if (_resetRefreshTask is not null)
            try { await _resetRefreshTask; } catch (OperationCanceledException) { }
        _resetClient.Dispose();
        await _service.DisposeAsync();
        _lifetime.Dispose();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (App.Current.IsExiting) return;
        e.Cancel = true;
        Hide();
        App.Current.NotifyRunningInBackground();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void FloatingWindow_Click(object sender, RoutedEventArgs e) => AiFloatingWindow.ShowOrActivate();

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow(this).ShowDialog();

    private void OfficialWebsite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://woliux.com/tools/aimaster") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this,
                L($"无法打开官网：{ex.Message}", $"Unable to open the official website: {ex.Message}"),
                L("进入官网", "Visit website"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(showUpToDate: true);

    private void ViewUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_latestRelease is not { } release) return;
        var releaseNotes = release.ReleaseNotes;
        if (string.IsNullOrWhiteSpace(releaseNotes))
        {
            OpenReleasePage(release.ReleasePageUrl);
            return;
        }

        var dialog = new Window
        {
            Owner = this,
            Title = L($"AIMaster {release.LatestVersion} 更新内容", $"What's new in AIMaster {release.LatestVersion}"),
            Icon = Icon,
            Width = 680,
            Height = 520,
            MinWidth = 480,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("AppBackground"),
            FontFamily = FontFamily
        };
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = L($"AIMaster {release.LatestVersion} · 发布说明", $"AIMaster {release.LatestVersion} · Release notes"),
            Foreground = (Brush)FindResource("Ink"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        };
        layout.Children.Add(heading);

        var notes = new TextBox
        {
            Text = releaseNotes.Trim(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = (Brush)FindResource("InputSurface"),
            Foreground = (Brush)FindResource("Ink"),
            BorderBrush = (Brush)FindResource("LineStrong"),
            Padding = new Thickness(14),
            FontSize = 13
        };
        Grid.SetRow(notes, 1);
        layout.Children.Add(notes);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var pageButton = new Button
        {
            Content = L("在 GitHub 查看", "View on GitHub"),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Background = (Brush)FindResource("ButtonSurface"),
            Foreground = (Brush)FindResource("Ink"),
            BorderBrush = (Brush)FindResource("LineStrong"),
            Cursor = Cursors.Hand
        };
        pageButton.Click += (_, _) => OpenReleasePage(release.ReleasePageUrl);
        actions.Children.Add(pageButton);
        var closeButton = new Button
        {
            Content = L("关闭", "Close"),
            Padding = new Thickness(14, 7, 14, 7),
            Background = (Brush)FindResource("ButtonSurface"),
            Foreground = (Brush)FindResource("Ink"),
            BorderBrush = (Brush)FindResource("LineStrong"),
            Cursor = Cursors.Hand,
            IsDefault = true
        };
        closeButton.Click += (_, _) => dialog.Close();
        actions.Children.Add(closeButton);
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    private void OpenReleasePage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this,
                L($"无法打开发布页面：{ex.Message}", $"Unable to open the release page: {ex.Message}"),
                L("查看更新内容", "What's new"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) =>
        await InstallUpdateAsync();

    private async Task CheckForUpdatesAsync(bool showUpToDate)
    {
        if (_checkingForUpdates) return;
        _checkingForUpdates = true;
        CheckUpdateButton.IsEnabled = false;
        try
        {
            var result = await GitHubUpdateChecker.CheckAsync(
                "PN-BUG", "AI-Master", typeof(AiManagerWindow).Assembly, _lifetime.Token,
                [
                    "AIMaster-win-x64-lightweight.zip",
                    "AIMaster-win-x64-standalone.zip",
                    "AIMaster-win-arm64-lightweight.zip",
                    "AIMaster-win-arm64-standalone.zip"
                ]);
            if (!result.ReleaseFound)
            {
                _latestRelease = null;
                _availableUpdate = null;
                ViewUpdateButton.Visibility = Visibility.Collapsed;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                if (showUpToDate)
                    SetFooterStatus(L("AIMaster 目前还没有已发布的 GitHub Release。",
                        "AIMaster does not have a published GitHub Release yet."));
                return;
            }
            _latestRelease = result;
            ViewUpdateButton.Visibility = Visibility.Visible;
            if (!result.UpdateAvailable)
            {
                _availableUpdate = null;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                if (showUpToDate)
                    SetFooterStatus(L($"AIMaster 已是最新版（{result.CurrentVersion}）。",
                        $"AIMaster is up to date ({result.CurrentVersion})."));
                return;
            }

            _availableUpdate = result;
            InstallUpdateButton.Visibility = Visibility.Visible;
            SetFooterStatus(L($"发现 AIMaster {result.LatestVersion}，请在“设置”页更新。",
                $"AIMaster {result.LatestVersion} is available. Open Settings to update."));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (showUpToDate)
                SetFooterStatus(L($"无法检查更新：{ex.Message}", $"Unable to check for updates: {ex.Message}"), warning: true);
        }
        finally
        {
            _checkingForUpdates = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is null) return;
        var update = _availableUpdate;
        var confirmed = WpfMessageBox.Show(this,
            L($"现在下载并安装 AIMaster {update.LatestVersion}？\n\n程序将关闭并自动重启，本地设置会保留。",
              $"Download and install AIMaster {update.LatestVersion} now?\n\nThe app will close and restart. Local settings will be preserved."),
            L("安装更新", "Install update"), MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirmed != MessageBoxResult.Yes) return;

        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        var progress = new Progress<double>(value =>
            SyncStatusText.Text = L($"下载更新 {value:P0}", $"Downloading {value:P0}"));
        try
        {
            await ApplicationUpdater.PrepareAndLaunchAsync(update,
                new SelfUpdateOptions("AIMaster", "lightweight", PreserveToolkitConfiguration: false,
                    LocalizationService.IsEnglish), progress, _lifetime.Token);
            App.Current.ExitApplication();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SyncStatusText.Text = L("更新失败", "Update failed");
            var open = WpfMessageBox.Show(this,
                L($"自动更新失败：{ex.Message}\n\n是否改为打开下载页面？",
                  $"Automatic update failed: {ex.Message}\n\nOpen the download page instead?"),
                L("更新失败", "Update failed"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (open == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(update.ReleasePageUrl) { UseShellExecute = true });
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
        }
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        var language = LocalizationService.IsEnglish ? LocalizationService.Chinese : LocalizationService.English;
        LocalizationService.SetLanguage(language);
        var settings = _service.ReloadSettings();
        settings.Language = language;
        _service.SaveSettings(settings);
    }

    private void UpdateLanguageButton()
    {
        LanguageButton.Content = LocalizationService.IsEnglish ? "中文" : "EN";
        LanguageButton.ToolTip = LocalizationService.IsEnglish ? "Switch to Chinese" : "切换到英语";
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var next = _theme == ThemeModes.Dark ? ThemeModes.Light : ThemeModes.Dark;
        var settings = _service.ReloadSettings();
        settings.Theme = next;
        _service.SaveSettings(settings);
        ApplyTheme(next, refreshContent: true);
        AiFloatingWindow.ApplySavedTheme(next);
    }

    private void ApplyTheme(string? theme, bool refreshContent)
    {
        _theme = ThemeModes.Normalize(theme);
        ThemeService.Apply(Resources, _theme);
        UpdateThemeButton();
        if (refreshContent && _service.LastSnapshot is { } snapshot)
            RenderSnapshot(snapshot);
    }

    private void UpdateThemeButton()
    {
        if (ThemeButton is null) return;
        var dark = _theme == ThemeModes.Dark;
        ThemeButton.Content = dark ? L("☀ 日间", "☀ Light") : L("☾ 夜间", "☾ Dark");
        ThemeButton.ToolTip = dark ? L("切换到日间模式", "Switch to light mode") : L("切换到夜间模式", "Switch to dark mode");
    }

    private void InitializeDashboardCards()
    {
        var definitions = new[]
        {
            ("quota", "主额度跑道", QuotaCard),
            ("guard", "任务闸门", GuardCard),
            ("resetSignals", "重置信号", ResetSignalsCard),
            ("limits", "额度窗口", LimitsCard),
            ("forecast", "消耗预测 / 本周", ForecastCard),
            ("threads", "最近任务", ThreadsCard),
            ("settings", "保护策略", SettingsCard),
            ("localUsage", "本机消耗", LocalUsageCard),
            ("dailyUsage", "当日消耗 / 时段", DailyUsageCard),
            ("sharedUsage", "共享统计", SharedUsageCard)
        };

        foreach (var (id, title, card) in definitions)
        {
            DashboardGrid.Children.Remove(card);
            WrapDashboardCard(id, title, card);
        }

        DashboardGrid.Children.Remove(ApplicationSettingsCard);

        DashboardGrid.RowDefinitions.Clear();
        DashboardGrid.ColumnDefinitions.Clear();
        AddDashboardPage("quota", "查看账户剩余额度、额度窗口和重置信号。");
        AddDashboardPage("tasks", "查看最近任务，并管理当前连接可见任务的暂停状态。");
        AddDashboardPage("usage", "分析本机、项目和当日用量，查看本周消耗预测。");
        AddDashboardPage("shared", "配置设备同步，并比较同组设备的共享用量。", singleColumn: true);
        AddDashboardPage("settings", "调整保护策略、浮窗字号、语言和主题，管理版本更新。");
        _dashboardPages["settings"].RightColumn.Children.Add(ApplicationSettingsCard);
        ApplyDashboardLayout();
        QuotaPageButton.IsChecked = true;
    }

    private void AddDashboardPage(string id, string description, bool singleColumn = false)
    {
        var host = new Grid { Tag = id, Visibility = Visibility.Collapsed };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(singleColumn ? 0 : 14) });
        host.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = singleColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star)
        });
        var left = CreateDashboardColumn();
        var right = CreateDashboardColumn();
        Grid.SetColumn(right, 2);
        if (singleColumn) right.Visibility = Visibility.Collapsed;
        host.Children.Add(left);
        host.Children.Add(right);
        DashboardGrid.Children.Add(host);
        _dashboardPages.Add(id, new DashboardPageState(host, left, right, description));
    }

    private static string GetDashboardPageId(string cardId) => cardId switch
    {
        "quota" or "limits" or "resetSignals" => "quota",
        "threads" or "guard" => "tasks",
        "localUsage" or "dailyUsage" or "forecast" => "usage",
        "sharedUsage" => "shared",
        "settings" => "settings",
        _ => throw new ArgumentOutOfRangeException(nameof(cardId))
    };

    private void DashboardPage_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string id } || !_dashboardPages.TryGetValue(id, out var selected)) return;
        _dashboardPages[_activeDashboardPage].ScrollOffset = DashboardScrollViewer.VerticalOffset;
        _activeDashboardPage = id;
        foreach (var page in _dashboardPages.Values)
            page.Host.Visibility = ReferenceEquals(page, selected) ? Visibility.Visible : Visibility.Collapsed;
        UpdateDashboardPageLanguage();
        DashboardScrollViewer.UpdateLayout();
        DashboardScrollViewer.ScrollToVerticalOffset(selected.ScrollOffset);
    }

    private void UpdateDashboardPageLanguage()
    {
        SharedPageButton.Content = L("共享统计", "Shared usage");
        if (_dashboardPages.TryGetValue(_activeDashboardPage, out var page))
            PageDescriptionText.Text = LocalizationService.T(page.Description);
    }

    private StackPanel CreateDashboardColumn()
    {
        var column = new StackPanel { AllowDrop = true, Background = Brushes.Transparent };
        column.DragOver += DashboardColumn_DragOver;
        column.Drop += DashboardColumn_Drop;
        column.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Width <= 0) return;
            foreach (var card in column.Children.OfType<Border>()) card.MaxWidth = args.NewSize.Width;
        };
        return column;
    }

    private void WrapDashboardCard(string id, string title, Border card)
    {
        if (card.Child is not UIElement body) return;
        var originalTitle = FindTextBlock(body, title);
        if (originalTitle is not null) originalTitle.Visibility = Visibility.Collapsed;

        card.Child = null;
        card.Tag = id;
        card.Margin = new Thickness(0, 0, 0, 12);
        card.MinWidth = MinimumCardWidth;
        card.HorizontalAlignment = HorizontalAlignment.Stretch;
        card.ClipToBounds = true;
        card.AllowDrop = true;
        card.DragOver += DashboardCard_DragOver;
        card.Drop += DashboardCard_Drop;
        card.PreviewMouseLeftButtonDown += CardEdge_MouseLeftButtonDown;
        card.PreviewMouseLeftButtonUp += CardEdge_MouseLeftButtonUp;
        card.PreviewMouseMove += CardEdge_MouseMove;
        card.MouseLeave += (_, _) => card.ClearValue(CursorProperty);
        card.LostMouseCapture += (_, _) => _draggedCard = null;

        var frame = new Grid();
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid
        {
            Tag = id,
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 0, 4)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerTitle = new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("Label"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var helpContent = new StackPanel { MaxWidth = 340 };
        var interactionHelp = new TextBlock { TextWrapping = TextWrapping.Wrap };
        helpContent.Children.Add(interactionHelp);
        foreach (var hint in FindCardHelp(body))
        {
            hint.Visibility = Visibility.Collapsed;
            var helpText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            // Keep the source in the page so language and scope updates reach the tooltip.
            helpText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text))
            {
                Source = hint,
                Mode = System.Windows.Data.BindingMode.OneWay
            });
            helpContent.Children.Add(helpText);
        }
        var helpToolTip = new ToolTip { Content = helpContent };
        var help = new Button
        {
            Content = "?",
            Style = (Style)FindResource("CardChromeButton"),
            ToolTip = helpToolTip,
            Margin = new Thickness(8, 0, 5, 0)
        };
        ToolTipService.SetInitialShowDelay(help, 200);
        ToolTipService.SetShowDuration(help, 60000);
        Grid.SetColumn(help, 1);
        var toggle = new Button
        {
            Content = "⌃",
            Tag = id,
            Style = (Style)FindResource("CardChromeButton"),
            ToolTip = L("折叠卡片", "Collapse card")
        };
        toggle.Click += CardToggle_Click;
        Grid.SetColumn(toggle, 2);
        var resizeThumb = new Thumb
        {
            Tag = id,
            Style = (Style)FindResource("CardResizeThumb"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-12, 0, -12, -12)
        };
        resizeThumb.DragStarted += CardResize_DragStarted;
        resizeThumb.DragDelta += CardResize_DragDelta;
        resizeThumb.DragCompleted += CardResize_DragCompleted;
        resizeThumb.PreviewMouseDoubleClick += CardResize_MouseDoubleClick;
        Grid.SetRowSpan(resizeThumb, 2);
        Panel.SetZIndex(resizeThumb, 1);
        header.Children.Add(headerTitle);
        header.Children.Add(help);
        header.Children.Add(toggle);
        frame.Children.Add(header);
        Grid.SetRow(body, 1);
        frame.Children.Add(body);
        frame.Children.Add(resizeThumb);
        card.Child = frame;

        _dashboardCards[id] = new DashboardCardState(
            id, title, card, body, headerTitle, help, interactionHelp, toggle, resizeThumb, card.MinHeight);
    }

    private static IEnumerable<TextBlock> FindCardHelp(DependencyObject root)
    {
        if (root is TextBlock { Tag: "CardHelp" } hint) yield return hint;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var match in FindCardHelp(child)) yield return match;
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        if (root is TextBlock block && string.Equals(block.Text, text, StringComparison.Ordinal)) return block;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var match = FindTextBlock(child, text);
            if (match is not null) return match;
        }
        return null;
    }

    private void ApplyDashboardLayout()
    {
        var layout = NormalizeDashboardLayout(_service.Settings.DashboardCardLayout);
        foreach (var state in _dashboardCards.Values) RemoveCardFromParent(state.Card);
        foreach (var entry in layout)
        {
            var parts = entry.Split(':', 2);
            if (!_dashboardCards.TryGetValue(parts[1], out var state)) continue;
            var pageId = GetDashboardPageId(state.Id);
            var page = _dashboardPages[pageId];
            // Shared usage uses the full page; settings keeps application controls in the other column.
            var column = parts[0] == "right" && pageId is not ("shared" or "settings")
                ? page.RightColumn : page.LeftColumn;
            column.Children.Add(state.Card);
        }

        _collapsedDashboardCards.Clear();
        foreach (var id in _service.Settings.CollapsedDashboardCards)
            if (_dashboardCards.ContainsKey(id)) _collapsedDashboardCards.Add(id);
        foreach (var state in _dashboardCards.Values)
            SetCardCollapsed(state, _collapsedDashboardCards.Contains(state.Id));
    }

    internal static AiDashboardCardSize? NormalizeDashboardCardSize(AiDashboardCardSize? size)
    {
        if (size is null || !double.IsFinite(size.Width) || !double.IsFinite(size.Height) ||
            size.Width <= 0 || size.Height <= 0) return null;
        return new AiDashboardCardSize
        {
            Width = Math.Clamp(size.Width, MinimumCardWidth, 1600),
            Height = Math.Clamp(size.Height, MinimumCardHeight, MaximumCardHeight)
        };
    }

    internal static IReadOnlyList<string> NormalizeDashboardLayout(IEnumerable<string>? savedLayout)
    {
        var validIds = DefaultDashboardLayout
            .Select(entry => entry.Split(':', 2)[1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var entry in savedLayout ?? Array.Empty<string>())
        {
            var parts = entry.Split(':', 2);
            if (parts.Length != 2 || parts[0] is not ("left" or "right") ||
                !validIds.Contains(parts[1]) || !seen.Add(parts[1])) continue;
            result.Add($"{parts[0]}:{parts[1]}");
        }
        foreach (var entry in DefaultDashboardLayout)
        {
            var id = entry.Split(':', 2)[1];
            if (seen.Add(id)) result.Add(entry);
        }
        return result;
    }

    private static void RemoveCardFromParent(Border card)
    {
        if (card.Parent is Panel panel) panel.Children.Remove(card);
    }

    internal static bool IsCardMoveEdge(Point point, Size size, Thickness padding, bool collapsed)
    {
        if (point.X < 0 || point.Y < 0 || point.X > size.Width || point.Y > size.Height) return false;
        // The bottom edge belongs to the resize Thumb while a card is expanded.
        if (!collapsed && point.Y >= size.Height - 8) return false;
        return point.X <= padding.Left || point.X >= size.Width - padding.Right ||
               point.Y <= padding.Top || (collapsed && point.Y >= size.Height - padding.Bottom);
    }

    private void CardEdge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedCard = null;
        if (sender is not Border { Tag: string id } card || !_dashboardCards.ContainsKey(id) ||
            !IsCardMoveEdge(e.GetPosition(card), card.RenderSize, card.Padding, _collapsedDashboardCards.Contains(id))) return;
        _cardDragStart = e.GetPosition(this);
        if (card.CaptureMouse()) _draggedCard = card;
        e.Handled = true;
    }

    private void CardEdge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedCard is not { } card) return;
        _draggedCard = null;
        card.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CardEdge_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: string id } hovered) return;
        if (IsCardMoveEdge(e.GetPosition(hovered), hovered.RenderSize, hovered.Padding, _collapsedDashboardCards.Contains(id)))
            hovered.Cursor = Cursors.SizeAll;
        else
            hovered.ClearValue(CursorProperty);
        if (_draggedCard is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggedCard.ReleaseMouseCapture();
            _draggedCard = null;
            return;
        }
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _cardDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _cardDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var card = _draggedCard;
        _draggedCard = null;
        card.ReleaseMouseCapture();
        card.Opacity = 0.6;
        try { DragDrop.DoDragDrop(card, card, DragDropEffects.Move); }
        finally
        {
            card.Opacity = 1;
            card.ClearValue(CursorProperty);
        }
        e.Handled = true;
    }

    internal static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = GetParent(current);
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is ContentElement content)
            return ContentOperations.GetParent(content) ??
                   (content as FrameworkContentElement)?.Parent;
        if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(current);
        return LogicalTreeHelper.GetParent(current);
    }

    private void DashboardCard_DragOver(object sender, DragEventArgs e) => SetMoveEffect(e);

    private void DashboardColumn_DragOver(object sender, DragEventArgs e) => SetMoveEffect(e);

    private static void SetMoveEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(Border)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void DashboardCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border target || e.Data.GetData(typeof(Border)) is not Border source ||
            ReferenceEquals(source, target) || target.Parent is not StackPanel column) return;
        var targetIndex = column.Children.IndexOf(target);
        MoveDashboardCard(source, column, targetIndex);
        e.Handled = true;
    }

    private void DashboardColumn_Drop(object sender, DragEventArgs e)
    {
        if (sender is not StackPanel column || e.Data.GetData(typeof(Border)) is not Border source) return;
        MoveDashboardCard(source, column, column.Children.Count);
        e.Handled = true;
    }

    private void MoveDashboardCard(Border card, StackPanel targetColumn, int targetIndex)
    {
        if (card.Tag is not string id || !_dashboardCards.ContainsKey(id)) return;
        var page = _dashboardPages[GetDashboardPageId(id)];
        if (!ReferenceEquals(targetColumn, page.LeftColumn) && !ReferenceEquals(targetColumn, page.RightColumn)) return;
        if ((GetDashboardPageId(id) is "shared" or "settings") && !ReferenceEquals(targetColumn, page.LeftColumn)) return;
        if (card.Parent is StackPanel currentColumn)
        {
            var oldIndex = currentColumn.Children.IndexOf(card);
            currentColumn.Children.Remove(card);
            if (ReferenceEquals(currentColumn, targetColumn) && oldIndex < targetIndex) targetIndex--;
        }
        targetColumn.Children.Insert(Math.Clamp(targetIndex, 0, targetColumn.Children.Count), card);
        if (targetColumn.ActualWidth > 0) card.MaxWidth = targetColumn.ActualWidth;
        SaveDashboardLayout();
    }

    private void CardResize_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } ||
            !_dashboardCards.TryGetValue(id, out var state) || _collapsedDashboardCards.Contains(id)) return;
        state.Card.MinHeight = MinimumCardHeight;
        state.Card.Height = Math.Max(MinimumCardHeight, state.Card.ActualHeight);
        state.Card.Width = double.NaN;
        state.Card.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void CardResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } ||
            !_dashboardCards.TryGetValue(id, out var state) || _collapsedDashboardCards.Contains(id)) return;
        state.Card.Height = Math.Clamp(state.Card.Height + e.VerticalChange, MinimumCardHeight, MaximumCardHeight);
    }

    private void CardResize_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && _dashboardCards.TryGetValue(id, out var state))
            SaveDashboardCardSize(state);
    }

    private void CardResize_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || !_dashboardCards.TryGetValue(id, out var state)) return;
        var settings = _service.ReloadSettings();
        settings.DashboardCardSizes.Remove(id);
        _service.SaveSettings(settings);
        state.Card.Width = double.NaN;
        state.Card.Height = double.NaN;
        state.Card.MaxWidth = double.PositiveInfinity;
        state.Card.MinHeight = state.ExpandedMinHeight;
        state.Card.HorizontalAlignment = HorizontalAlignment.Stretch;
        e.Handled = true;
    }

    private void SaveDashboardCardSize(DashboardCardState state)
    {
        var size = NormalizeDashboardCardSize(new AiDashboardCardSize
        {
            Width = state.Card.ActualWidth,
            Height = state.Card.ActualHeight
        });
        if (size is null) return;
        var settings = _service.ReloadSettings();
        settings.DashboardCardSizes[state.Id] = size;
        _service.SaveSettings(settings);
    }

    private void ApplyDashboardCardSize(DashboardCardState state)
    {
        var size = _service.Settings.DashboardCardSizes.TryGetValue(state.Id, out var saved)
            ? NormalizeDashboardCardSize(saved)
            : null;
        if (size is null)
        {
            state.Card.Width = double.NaN;
            state.Card.Height = double.NaN;
            state.Card.MaxWidth = double.PositiveInfinity;
            state.Card.MinHeight = state.ExpandedMinHeight;
            state.Card.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }
        state.Card.MinHeight = MinimumCardHeight;
        // Width is intentionally fluid. Persisted fixed widths made cards look broken
        // after maximizing the window or moving between displays with different DPI.
        state.Card.Width = double.NaN;
        state.Card.Height = size.Height;
        state.Card.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void CardToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || !_dashboardCards.TryGetValue(id, out var state)) return;
        var collapsed = !_collapsedDashboardCards.Contains(id);
        if (collapsed) _collapsedDashboardCards.Add(id);
        else _collapsedDashboardCards.Remove(id);
        SetCardCollapsed(state, collapsed);
        SaveDashboardLayout();
    }

    private void SetCardCollapsed(DashboardCardState state, bool collapsed)
    {
        state.Body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        state.ResizeThumb.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (collapsed)
        {
            state.Card.Height = double.NaN;
            state.Card.MinHeight = 0;
        }
        else
        {
            ApplyDashboardCardSize(state);
        }
        state.Card.Padding = collapsed ? new Thickness(10, 7, 9, 7) : new Thickness(12);
        state.ToggleButton.Content = collapsed ? "⌄" : "⌃";
        state.ToggleButton.ToolTip = collapsed
            ? L("展开卡片", "Expand card")
            : L("折叠卡片", "Collapse card");
    }

    private void SaveDashboardLayout()
    {
        var settings = _service.ReloadSettings();
        settings.DashboardCardLayout = CaptureDashboardLayout().ToList();
        settings.CollapsedDashboardCards = _collapsedDashboardCards.OrderBy(id => id).ToList();
        _service.SaveSettings(settings);
    }

    internal IReadOnlyList<string> CaptureDashboardLayout() =>
        _dashboardPages.Values.SelectMany(page =>
                CaptureDashboardColumn(page.LeftColumn, "left")
                    .Concat(CaptureDashboardColumn(page.RightColumn, "right")))
            .ToArray();

    private static IEnumerable<string> CaptureDashboardColumn(Panel column, string name) =>
        column.Children.OfType<Border>()
            .Select(card => card.Tag as string)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => $"{name}:{id}");

    private void UpdateDashboardChromeLanguage()
    {
        foreach (var state in _dashboardCards.Values)
        {
            state.HeaderTitle.Text = LocalizationService.T(state.Title);
            var collapsed = _collapsedDashboardCards.Contains(state.Id);
            state.ToggleButton.ToolTip = collapsed
                ? L("展开卡片", "Expand card")
                : L("折叠卡片", "Collapse card");
            state.InteractionHelp.Text = L("按住卡片上边缘或两侧拖动排序；拖动下边缘调整高度，双击下边缘恢复自适应。",
                "Drag the top or side edges to rearrange cards. Drag the bottom edge to adjust height; double-click it to reset.");
            System.Windows.Automation.AutomationProperties.SetName(state.HelpButton, L("卡片帮助", "Card help"));
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        SyncStatusText.Text = L("正在读取 Codex…", "Reading Codex…");
        SyncStatusText.ToolTip = null;
        try
        {
            var snapshot = await _service.RefreshAsync(_lifetime.Token);
            RenderSnapshot(snapshot);
            ObserveAccountRecovery(snapshot);
            ApplyQuotaNotification(snapshot);
            await ApplyGuardAsync(snapshot);
            SyncStatusText.Text = L("已连接", "Connected");
            SyncStatusText.ToolTip = null;
            LastSyncText.Text = $"SYNC {snapshot.CapturedAt.LocalDateTime:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var message = BuildConnectionError(ex);
            SyncStatusText.Text = L("连接失败", "Connection failed");
            SyncStatusText.ToolTip = message;
            SetFooterStatus(message, warning: true);
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void RenderSnapshot(AiManagerSnapshot snapshot)
    {
        var main = snapshot.MainLimit;
        if (main != null)
        {
            RemainingText.Text = main.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture);
            MainUsageBar.Value = main.UsedPercent;
            MainUsedText.Text = LocalizationService.IsEnglish
                ? $"Used {main.UsedPercent:0.#}% · {LocalizationService.T(main.WindowName)}"
                : $"已用 {main.UsedPercent:0.#}% · {main.WindowName}";
            MainResetText.Text = main.ResetText;
            ApplyQuotaVisual(main.UsedPercent);
        }
        else
        {
            RemainingText.Text = "--";
            MainUsageBar.Value = 0;
            MainUsedText.Text = L("账户未返回额度窗口", "No quota window returned by the account");
            MainResetText.Text = string.Empty;
        }

        LimitItems.ItemsSource = snapshot.Limits;
        ThreadList.ItemsSource = snapshot.Threads;
        ResetCreditText.Text = snapshot.ResetCredits > 0
            ? (LocalizationService.IsEnglish ? $"Resets available × {snapshot.ResetCredits}" : $"可用重置 × {snapshot.ResetCredits}")
            : L("无可用重置", "No resets available");

        RenderForecast(snapshot);
        var weeklyLimit = snapshot.Limits
            .Where(item => item.WindowDurationMinutes is >= 6 * 24 * 60 and <= 8 * 24 * 60)
            .OrderBy(item => Math.Abs(item.WindowDurationMinutes - 7 * 24 * 60))
            .FirstOrDefault();
        RenderLocalUsage(snapshot.LocalUsage, snapshot.DailyUsage, weeklyLimit?.UsedPercent);
        RenderSharedUsage(snapshot.SharedUsage);
        RenderDailyQuota(snapshot);
        SetFooterStatus(snapshot.LifetimeTokens is { } lifetime
            ? (LocalizationService.IsEnglish ? $"Lifetime {FormatTokens(lifetime)} tokens · From Codex App Server" : $"累计 {FormatTokens(lifetime)} tokens · 数据来自 Codex App Server")
            : L("数据来自 Codex App Server；认证由 Codex 管理。", "Data comes from Codex App Server; authentication is managed by Codex."));
        LocalizationService.Apply(this);
    }

    private void ForecastScope_Click(object sender, RoutedEventArgs e)
    {
        var showLocal = ReferenceEquals(sender, LocalForecastButton);
        if (_showLocalForecast == showLocal) return;
        _showLocalForecast = showLocal;
        UpdateForecastScopeButtons();
        if (_service.LastSnapshot is { } snapshot) RenderForecast(snapshot);
    }

    private void UpdateForecastScopeButtons()
    {
        AccountForecastButton.Style = (Style)FindResource(_showLocalForecast ? "ActionButton" : "SignalButton");
        LocalForecastButton.Style = (Style)FindResource(_showLocalForecast ? "SignalButton" : "ActionButton");
    }

    private void RenderForecast(AiManagerSnapshot snapshot)
    {
        var forecast = _showLocalForecast
            ? _service.BuildLocalForecast(snapshot)
            : _service.BuildForecast(snapshot);
        ForecastSummaryText.Text = forecast.Summary;
        ForecastSummaryText.Foreground = ThemeBrush(forecast.ExhaustsBeforeReset ? "Warning" : "Ink");
        ForecastScopeNoteText.Text = _showLocalForecast
            ? L("以账号当前剩余额度为起点，仅按本机速度推演", "Starts from the account's current remaining quota and projects only this device's rate")
            : L("汇总该账号所有设备的用量", "Includes usage from all devices on this account");
        DailyTokensText.Text = FormatTokens(forecast.DailyTokens);
        DailyPercentText.Text = forecast.DailyPercent > 0 ? $"{forecast.DailyPercent:0.#}%" : "--";
        _weeklyRemainingForecast = AiManagerService.BuildWeeklyRemainingForecast(snapshot, forecast);
        RenderRemainingForecastChart();
        RenderUsageBars(_showLocalForecast ? snapshot.LocalUsage.DailyUsage : snapshot.DailyUsage);
    }

    private void ApplyQuotaNotification(AiManagerSnapshot snapshot)
    {
        var settings = _service.Settings;
        RenderDailyQuota(snapshot, notify: true);
        if (_quotaNotifications.Evaluate(snapshot.Limits, settings) is { } notification)
            App.Current.ShowQuotaNotification(notification,
                notification.IsPauseLimit ? settings.PausePercent : settings.WarningPercent);
    }

    private async Task ApplyGuardAsync(AiManagerSnapshot snapshot)
    {
        var highest = snapshot.Limits.Count == 0 ? 0 : snapshot.Limits.Max(item => item.UsedPercent);
        var overPause = highest >= _service.Settings.PausePercent;
        if (!overPause)
        {
            _overrideCurrentBreach = false;
            _alertedForCurrentBreach = false;
            if (!_localGuardPaused) SetGuardVisual(false, L("额度安全，当前无需暂停", "Quota safe — no pause needed"), HighestUsage(highest));
            return;
        }

        if (!_service.Settings.AutoPause || _overrideCurrentBreach)
        {
            SetGuardVisual(false, L("已超过暂停线，但当前仍放行", "Over the pause limit, but currently allowed"), HighestUsage(highest));
            return;
        }

        _localGuardPaused = true;
        SetGuardVisual(true, L("已超出限制，任务闸门暂停", "Limit exceeded — task guard paused"),
            LocalizationService.IsEnglish ? $"Highest usage {highest:0.#}% · Pause limit {_service.Settings.PausePercent:0.#}%" : $"最高窗口占用 {highest:0.#}% · 暂停线 {_service.Settings.PausePercent:0.#}%");
        if (_alertedForCurrentBreach) return;
        _alertedForCurrentBreach = true;
        var interrupted = await _service.PauseActiveThreadsAsync(_lifetime.Token);
        GuardDetailText.Text = LocalizationService.IsEnglish
            ? $"Interrupted {interrupted} visible task(s). Check other Codex windows manually."
            : $"已中断 {interrupted} 个可见任务；其他 Codex 窗口请手动确认。";
        SetFooterStatus(L($"额度超限，已暂停闸门并中断 {interrupted} 个可见任务。",
            $"Limit exceeded; guard paused and {interrupted} visible task(s) interrupted."), warning: true);
    }

    private void ApplyQuotaVisual(double used)
    {
        if (used >= _service.Settings.PausePercent)
        {
            QuotaStateText.Text = L("暂停线", "Pause limit");
            QuotaStateText.Foreground = ThemeBrush("DangerText");
            QuotaStateBadge.Background = ThemeBrush("DangerSoft");
            MainUsageBar.Foreground = ThemeBrush("Danger");
        }
        else if (used >= _service.Settings.WarningPercent)
        {
            QuotaStateText.Text = L("接近限制", "Near limit");
            QuotaStateText.Foreground = ThemeBrush("WarningText");
            QuotaStateBadge.Background = ThemeBrush("WarningSoft");
            MainUsageBar.Foreground = ThemeBrush("Warning");
        }
        else
        {
            QuotaStateText.Text = L("额度安全", "Quota safe");
            QuotaStateText.Foreground = ThemeBrush("SignalText");
            QuotaStateBadge.Background = ThemeBrush("SignalSoft");
            MainUsageBar.Foreground = ThemeBrush("Signal");
        }
    }

    private void SetGuardVisual(bool paused, string status, string detail)
    {
        _localGuardPaused = paused;
        GuardGlyph.Text = paused ? "HOLD" : "GO";
        GuardGlyph.Foreground = ThemeBrush(paused ? "Danger" : "Signal");
        GuardStatusText.Text = status;
        GuardDetailText.Text = detail;
        ResumeGuardButton.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PauseNow_Click(object sender, RoutedEventArgs e)
    {
        PauseNowButton.IsEnabled = false;
        try
        {
            var interrupted = await _service.PauseActiveThreadsAsync(_lifetime.Token);
            SetGuardVisual(true,
                L("已手动暂停任务闸门", "Task guard paused manually"),
                LocalizationService.IsEnglish
                    ? $"Interrupted {interrupted} running task(s) visible to this connection"
                    : $"已中断当前连接可见的 {interrupted} 个运行任务");
            SetFooterStatus(L($"已暂停闸门并中断 {interrupted} 个可见任务；其他窗口请手动确认。",
                $"Guard paused; {interrupted} visible task(s) interrupted. Check other windows manually."));
        }
        catch (Exception ex)
        {
            SetFooterStatus(L("暂停失败：", "Unable to pause tasks: ") + BuildConnectionError(ex), warning: true);
        }
        finally
        {
            PauseNowButton.IsEnabled = true;
        }
    }

    private void ResumeGuard_Click(object sender, RoutedEventArgs e)
    {
        _overrideCurrentBreach = true;
        SetGuardVisual(false,
            L("已人工解除本地闸门", "Local guard released manually"),
            L("本次超限期间不会再次自动暂停；额度回落后自动恢复保护",
                "Automatic pausing is disabled for this breach and will resume after usage falls below the limit"));
    }

    private async void SavePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadPercent(WarningPercentBox.Text, out var warning) ||
            !TryReadPercent(PausePercentBox.Text, out var pause) || warning >= pause ||
            !TryReadFloatingFontPercent(FloatingFontPercentBox.Text, out var floatingFontScale))
        {
            SetFooterStatus(L("策略无效：预警/暂停线需在 1–99 之间，浮窗字号需在 100–150% 之间，且预警线低于暂停线。",
                "Invalid policy: guard limits must be 1–99, font size 100–150%, and warning below pause."), warning: true);
            return;
        }

        var dailyEnabled = DailyQuotaWarningCheck.IsChecked == true;
        var dailyValid = TryReadDailyQuotaPercent(DailyQuotaLimitBox.Text, out var dailyPercent);
        if (dailyEnabled && !dailyValid)
        {
            SetFooterStatus(L("每日预警线需在 0.1–100% 之间。", "Daily warning must be between 0.1% and 100%."), warning: true);
            return;
        }
        var settings = _service.ReloadSettings();
        settings.DailyQuotaWarningEnabled = dailyEnabled;
        if (dailyValid) settings.DailyQuotaLimitPercent = dailyPercent;
        settings.WarningPercent = warning;
        settings.PausePercent = pause;
        settings.AutoPause = AutoPauseCheck.IsChecked == true;
        settings.QuotaNotificationsEnabled = QuotaNotificationsCheck.IsChecked == true;
        settings.FloatingFontScale = floatingFontScale;
        _service.SaveSettings(settings);
        AiFloatingWindow.ApplySavedFontScale(floatingFontScale);
        SetFooterStatus(L("设置已保存并应用", "Settings saved and applied"));
        if (_service.LastSnapshot is { } snapshot)
        {
            ApplyQuotaVisual(snapshot.MainLimit?.UsedPercent ?? 0);
            ApplyQuotaNotification(snapshot);
            await ApplyGuardAsync(snapshot);
        }
    }

    private void LoadPolicyControls()
    {
        WarningPercentBox.Text = _service.Settings.WarningPercent.ToString("0.#", CultureInfo.InvariantCulture);
        PausePercentBox.Text = _service.Settings.PausePercent.ToString("0.#", CultureInfo.InvariantCulture);
        FloatingFontPercentBox.Text = (_service.Settings.FloatingFontScale * 100)
            .ToString("0", CultureInfo.InvariantCulture);
        AutoPauseCheck.IsChecked = _service.Settings.AutoPause;
        QuotaNotificationsCheck.IsChecked = _service.Settings.QuotaNotificationsEnabled;
        DailyQuotaWarningCheck.IsChecked = _service.Settings.DailyQuotaWarningEnabled;
        DailyQuotaLimitBox.Text = _service.Settings.DailyQuotaLimitPercent.ToString("0.#", CultureInfo.InvariantCulture);
    }

    internal void LoadSharedUsageControls()
    {
        SharedUsageEnabledCheck.IsChecked = _service.Settings.SharedUsageEnabled;
        SharedUsageServerUrlBox.Text = _service.Settings.SharedUsageServerUrl;
        SharedUsageDeviceNameBox.Text = _service.Settings.SharedUsageDeviceName;
        SharedUsageSyncKeyBox.Password = _service.Settings.SharedUsageSyncKey;
        LoadSharedUsageResetMode();
    }

    private void GenerateSharedSyncKey_Click(object sender, RoutedEventArgs e)
    {
        SharedUsageSyncKeyBox.Password = LocalSecretProtection.GenerateSyncKey();
        SharedUsageStatusText.Text = L(
            "已生成新密钥；请保存并复制到其他设备。",
            "A new key was generated; save it and copy it to your other devices.");
    }

    private void CopySharedSyncKey_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SharedUsageSyncKeyBox.Password)) return;
        try
        {
            Clipboard.SetText(SharedUsageSyncKeyBox.Password);
            SharedUsageStatusText.Text = L("同步密钥已复制", "Sync key copied");
        }
        catch (ExternalException)
        {
            SharedUsageStatusText.Text = L("剪贴板正忙，请稍后重试", "The clipboard is busy; try again shortly");
        }
    }

    private async void SaveSharedUsage_Click(object sender, RoutedEventArgs e)
    {
        var enabled = SharedUsageEnabledCheck.IsChecked == true;
        var serverUrl = SharedUsageServerUrlBox.Text.Trim();
        var deviceName = SharedUsageDeviceNameBox.Text.Trim();
        var syncKey = SharedUsageSyncKeyBox.Password.Trim();
        if (enabled && (!SharedUsageClient.TryBuildSyncEndpoint(serverUrl, out _, out var error) ||
                        string.IsNullOrWhiteSpace(deviceName) || syncKey.Length is < 32 or > 512))
        {
            if (string.IsNullOrWhiteSpace(error))
                error = string.IsNullOrWhiteSpace(deviceName)
                    ? L("设备名称不能为空", "The device name is required")
                    : L("同步密钥需要 32–512 个字符", "The sync key must contain 32–512 characters");
            SharedUsageStatusText.Text = error;
            SetFooterStatus(L("共享设置无效：", "Invalid sharing settings: ") + error, warning: true);
            return;
        }

        if (_sharedUsageEditing) return;
        SetSharedUsageBusy(true);
        try
        {
            var settings = _service.ReloadSettings();
            settings.SharedUsageEnabled = enabled;
            settings.SharedUsageServerUrl = serverUrl;
            settings.SharedUsageDeviceName = deviceName;
            settings.SharedUsageSyncKey = syncKey;
            settings.SharedUsageResetMode = SelectedSharedUsageResetMode();
            _service.SaveSettings(settings);
            if (!enabled)
            {
                RenderSharedUsage(new AiSharedUsageSummary { Enabled = false });
                SharedUsageStatusText.Text = L("共享统计已关闭", "Shared usage is disabled");
                return;
            }

            SharedUsageStatusText.Text = L("正在同步设备数据…", "Syncing device usage…");
            var sharedUsage = await _service.SyncSharedUsageAsync(_lifetime.Token);
            RenderSharedUsage(sharedUsage);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SharedUsageStatusText.Text = L("共享设置或计数保存失败：", "Unable to save sharing settings or counts: ") + ex.Message;
            SetFooterStatus(SharedUsageStatusText.Text, warning: true);
        }
        finally
        {
            SetSharedUsageBusy(false);
        }
    }

    private void RenderSharedUsage(AiSharedUsageSummary usage)
    {
        var countedFrom = usage.CountedFrom;
        if (countedFrom is null && _service.Settings.SharedUsageResetMode == SharedUsageResetModes.Weekly)
        {
            var today = DateTime.Today;
            countedFrom = new DateTimeOffset(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)));
        }
        SharedUsagePeriodText.Text = countedFrom is { } from
            ? L($"当前设备统计范围：{from.LocalDateTime:yyyy-MM-dd HH:mm} 至今",
                $"This device's period: {from.LocalDateTime:yyyy-MM-dd HH:mm} to now")
            : _service.Settings.SharedUsageResetMode == SharedUsageResetModes.AccountReset
                ? L("当前设备统计范围：等待官方周额度重置时间", "This device's period: awaiting the official weekly reset time")
                : L("当前设备统计范围：全部本地历史", "This device's period: all local history");
        SharedUsageRestoreButton.IsEnabled = !_sharedUsageEditing && _service.CanRestoreSharedUsage;
        SharedDeviceItems.ItemsSource = null;
        SharedDeviceItems.ItemsSource = usage.Devices;
        SharedDeviceItems.Visibility = usage.Devices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SharedUsageEmptyText.Visibility = usage.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SharedUsageSummaryText.Text = usage.Devices.Count == 0
            ? string.Empty
            : LocalizationService.IsEnglish
                ? $"{usage.Devices.Count} devices · {FormatTokens(usage.TotalTokens)} tokens"
                : $"{usage.Devices.Count} 台设备 · {FormatTokens(usage.TotalTokens)} Token";
        SharedUsageStatusText.Text = !usage.Enabled
            ? L("启用后可查看使用同一同步密钥的设备", "Enable sharing to see devices using the same sync key")
            : !string.IsNullOrWhiteSpace(usage.Error)
                ? (LocalizationService.IsEnglish ? $"Sync failed: {usage.Error}" : $"同步失败：{usage.Error}")
                : usage.SyncedAt is { } syncedAt
                    ? (LocalizationService.IsEnglish
                        ? $"Synced {syncedAt.LocalDateTime:HH:mm:ss}"
                        : $"已同步 {syncedAt.LocalDateTime:HH:mm:ss}")
                    : L("等待首次同步", "Waiting for first sync");
    }

    private void RenderUsageBars(IEnumerable<AiDailyUsage> usage)
    {
        UsageBarsPanel.Children.Clear();
        var byDate = usage.ToDictionary(item => item.Date, item => item.Tokens);
        var days = Enumerable.Range(0, 7)
            .Select(offset => DateOnly.FromDateTime(DateTime.Today.AddDays(offset - 6)))
            .Select(date => new AiDailyUsage { Date = date, Tokens = byDate.GetValueOrDefault(date) })
            .ToList();
        var max = Math.Max(1, days.Max(item => item.Tokens));
        foreach (var day in days)
        {
            var column = new StackPanel
            {
                Width = 36, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.Transparent
            };
            SetChartToolTip(column, LocalizationService.IsEnglish
                ? $"{day.Date.ToString("MMM d", CultureInfo.InvariantCulture)} · {day.Tokens:N0} tokens"
                : $"{day.Date:M月d日} · {day.Tokens:N0} Token");
            column.Children.Add(new Border
            {
                Height = 8 + 54d * day.Tokens / max,
                Background = ThemeBrush(day.Date == DateOnly.FromDateTime(DateTime.Today) ? "Signal" : "ChartBar"),
                CornerRadius = new CornerRadius(3)
            });
            column.Children.Add(new TextBlock
            {
                Text = day.Date.Day.ToString(CultureInfo.InvariantCulture),
                Foreground = ThemeBrush("Subtle"),
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0)
            });
            UsageBarsPanel.Children.Add(column);
        }
    }

    private void RenderLocalUsage(AiLocalUsageSummary usage, IReadOnlyCollection<AiDailyUsage> accountDailyUsage,
        double? weeklyUsedPercent)
    {
        var estimate = EstimateLocalQuotaUsage(usage, accountDailyUsage, weeklyUsedPercent);
        LocalUsageTokensText.Text = FormatTokens(usage.TotalTokens);
        LocalUsageModelText.Text = usage.MostUsedModel ?? LocalizationService.L("未知", "Unknown");
        DailyUsageTokensText.Text = FormatTokens(usage.TodayTokens);
        var todayQuotaPercent = EstimateTodayWeeklyQuotaPercent(usage, estimate);
        DailyUsageShareText.Text = todayQuotaPercent is { } percent
            ? LocalizationService.IsEnglish
                ? $"~{percent:0.##}% of total weekly quota"
                : $"约占周总额度 {percent:0.##}%"
            : LocalizationService.L("周总额度占比暂不可用", "Weekly quota share unavailable");
        _todayHourlyUsage = usage.TodayHourlyUsage;
        DailyUsageEmptyText.Visibility = usage.TodayTokens > 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderTodayUsageChart();
        LocalUsageDetailText.Text = LocalizationService.IsEnglish
            ? $"{usage.SessionCount} local sessions · {usage.Projects.Count} projects"
            : $"{usage.SessionCount} 个本机会话 · {usage.Projects.Count} 个项目";
        LocalUsageEstimateText.Text = estimate.AccountTokenSharePercent is { } accountShare
            ? estimate.EstimatedQuotaPercent is { } quota
                ? LocalizationService.IsEnglish
                    ? $"~{accountShare:0.#}% of account tokens · ~{quota:0.##}% weekly quota"
                    : $"约占账户 {accountShare:0.#}% Token · 约 {quota:0.##}% 周额度"
                : LocalizationService.IsEnglish
                    ? $"~{accountShare:0.#}% of account tokens · quota estimate unavailable"
                    : $"约占账户 {accountShare:0.#}% Token · 周额度估算不可用"
            : usage.TotalTokens > 0
                ? LocalizationService.L("账户 Token 历史不足，仅显示本机原始 Token",
                    "Account token history is incomplete; showing local raw tokens only")
                : LocalizationService.L("本周暂无本机 Token", "No local tokens this week");
        ProjectUsageCountText.Text = usage.Projects.Count > 6
            ? (LocalizationService.IsEnglish ? $"Top 6 of {usage.Projects.Count}" : $"前 6 / 共 {usage.Projects.Count}")
            : (LocalizationService.IsEnglish ? $"{usage.Projects.Count} projects" : $"共 {usage.Projects.Count} 个项目");
        var visibleProjects = BuildProjectQuotaUsage(usage, estimate.EstimatedQuotaPercent).Take(6).ToList();
        ProjectUsageItems.ItemsSource = visibleProjects;
        ProjectUsageItems.Visibility = visibleProjects.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        LocalUsageEmptyText.Visibility = visibleProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DailyUsageCanvas_SizeChanged(object sender, SizeChangedEventArgs e) =>
        RenderTodayUsageChart();

    private void RenderTodayUsageChart()
    {
        DailyUsageCanvas.Children.Clear();
        var width = DailyUsageCanvas.ActualWidth;
        var height = DailyUsageCanvas.ActualHeight;
        if (width < 120 || height < 70 || _todayHourlyUsage.Count == 0) return;
        var byHour = _todayHourlyUsage.ToDictionary(item => item.Hour, item => item.Tokens);
        var maximum = Math.Max(1, byHour.Values.DefaultIfEmpty().Max());
        const double left = 42;
        const double top = 8;
        const double right = 8;
        const double bottom = 24;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        foreach (var fraction in new[] { 0d, 0.5d, 1d })
        {
            var y = top + plotHeight * (1 - fraction);
            DailyUsageCanvas.Children.Add(new Line
            {
                X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y,
                Stroke = ThemeBrush("ChartGrid"), StrokeThickness = 1
            });
        }

        AddCanvasText(DailyUsageCanvas, FormatTokens(maximum), 0, top - 5, "Subtle", 10);
        AddCanvasText(DailyUsageCanvas, "0", 24, top + plotHeight - 7, "Subtle", 10);
        var points = Enumerable.Range(0, 24)
            .Select(hour => new Point(
                left + plotWidth * hour / 23d,
                top + plotHeight * (1 - byHour.GetValueOrDefault(hour) / (double)maximum)))
            .ToList();
        DailyUsageCanvas.Children.Add(new Polyline
        {
            Points = new PointCollection(points),
            Stroke = ThemeBrush("Signal"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });

        foreach (var hour in new[] { 0, 6, 12, 18, 23 })
            AddCanvasText(DailyUsageCanvas, $"{hour:00}", points[hour].X - 6,
                height - bottom + 5, "Subtle", 10);
        foreach (var item in _todayHourlyUsage.Where(item => item.Tokens > 0))
        {
            var point = points[Math.Clamp(item.Hour, 0, 23)];
            var marker = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = ThemeBrush("Signal"),
                Stroke = ThemeBrush("Panel"),
                StrokeThickness = 1
            };
            Canvas.SetLeft(marker, point.X - 3);
            Canvas.SetTop(marker, point.Y - 3);
            DailyUsageCanvas.Children.Add(marker);
        }
        AddChartHoverTargets(DailyUsageCanvas, points, Enumerable.Range(0, 24)
            .Select(hour => LocalizationService.IsEnglish
                ? $"{hour:00}:00–{hour:00}:59 · {byHour.GetValueOrDefault(hour):N0} tokens"
                : $"{hour:00}:00–{hour:00}:59 · {byHour.GetValueOrDefault(hour):N0} Token")
            .ToList(), top, height - top);
    }

    private static void SetChartToolTip(FrameworkElement target, string text)
    {
        target.ToolTip = text;
        ToolTipService.SetInitialShowDelay(target, 0);
        ToolTipService.SetBetweenShowDelay(target, 0);
        ToolTipService.SetShowDuration(target, 60000);
    }

    private static void AddChartHoverTargets(
        Canvas canvas, IReadOnlyList<Point> points, IReadOnlyList<string> labels, double top, double height)
    {
        // Each date/hour owns the area up to the midpoint of its neighbours,
        // so the line and empty space are as easy to hover as the small markers.
        for (var index = 0; index < points.Count; index++)
        {
            var left = index == 0 ? Math.Max(0, points[index].X - 8)
                : (points[index - 1].X + points[index].X) / 2;
            var right = index == points.Count - 1 ? Math.Min(canvas.ActualWidth, points[index].X + 8)
                : (points[index].X + points[index + 1].X) / 2;
            var target = new Border
            {
                Width = Math.Max(1, right - left), Height = height,
                Background = Brushes.Transparent
            };
            SetChartToolTip(target, labels[index]);
            Canvas.SetLeft(target, left);
            Canvas.SetTop(target, top);
            canvas.Children.Add(target);
        }
    }

    private void AddCanvasText(
        Canvas canvas, string text, double left, double top, string brushKey, double fontSize)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = ThemeBrush(brushKey),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = fontSize
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        canvas.Children.Add(label);
    }

    internal static AiLocalQuotaEstimate EstimateLocalQuotaUsage(
        AiLocalUsageSummary usage, IEnumerable<AiDailyUsage> accountDailyUsage, double? weeklyUsedPercent)
    {
        var accountTokens = accountDailyUsage
            .Where(item => item.Date >= usage.PeriodStart && item.Date <= usage.PeriodEnd)
            .Sum(item => item.Tokens);
        if (accountTokens <= 0 || usage.TotalTokens < 0 || usage.TotalTokens > accountTokens)
            return new AiLocalQuotaEstimate(accountTokens, null, null);

        var accountShare = usage.TotalTokens * 100d / accountTokens;
        var quota = weeklyUsedPercent is { } value
            ? Math.Clamp(value, 0, 100) * accountShare / 100d
            : (double?)null;
        return new AiLocalQuotaEstimate(accountTokens, accountShare, quota);
    }

    internal static double? EstimateTodayWeeklyQuotaPercent(
        AiLocalUsageSummary usage, AiLocalQuotaEstimate estimate)
    {
        if (usage.TotalTokens <= 0 || usage.TodayTokens < 0 || usage.TodayTokens > usage.TotalTokens ||
            estimate.EstimatedQuotaPercent is not { } localQuotaPercent)
            return null;
        return Math.Clamp(localQuotaPercent, 0, 100) * usage.TodayTokens / usage.TotalTokens;
    }

    internal static IReadOnlyList<AiProjectUsage> BuildProjectQuotaUsage(
        AiLocalUsageSummary usage, double? estimatedLocalQuotaPercent)
    {
        var normalizedLocalQuota = estimatedLocalQuotaPercent is { } value
            ? Math.Clamp(value, 0, 100)
            : (double?)null;
        return usage.Projects.Select(item => new AiProjectUsage
        {
            ProjectName = item.ProjectName,
            ProjectPath = item.ProjectPath,
            Tokens = item.Tokens,
            SharePercent = item.SharePercent,
            SessionCount = item.SessionCount,
            MostUsedModel = item.MostUsedModel,
            WeeklyQuotaPercent = normalizedLocalQuota * item.SharePercent / 100d
        }).ToList();
    }

    private void RemainingForecastCanvas_SizeChanged(object sender, SizeChangedEventArgs e) =>
        RenderRemainingForecastChart();

    private void RenderRemainingForecastChart()
    {
        RemainingForecastCanvas.Children.Clear();
        var width = RemainingForecastCanvas.ActualWidth;
        var height = RemainingForecastCanvas.ActualHeight;
        if (width < 100 || height < 60 || _weeklyRemainingForecast.Count == 0) return;

        const double left = 34;
        const double top = 7;
        const double right = 6;
        const double bottom = 24;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        foreach (var percent in new[] { 100d, 50d, 0d })
        {
            var y = top + (100 - percent) / 100 * plotHeight;
            RemainingForecastCanvas.Children.Add(new Line
            {
                X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y,
                Stroke = ThemeBrush("ChartGrid"), StrokeThickness = 1
            });
            AddChartText($"{percent:0}", 0, y - 7, "Subtle", 10);
        }

        var points = _weeklyRemainingForecast
            .Select((item, index) => new Point(
                left + plotWidth * index / Math.Max(1, _weeklyRemainingForecast.Count - 1),
                top + (100 - item.RemainingPercent) / 100 * plotHeight))
            .ToList();
        var todayIndex = _weeklyRemainingForecast.TakeWhile(item => !item.IsProjected).Count() - 1;
        todayIndex = Math.Clamp(todayIndex, 0, points.Count - 1);

        AddForecastLine(points.Take(todayIndex + 1), "Signal", null);
        AddForecastLine(points.Skip(todayIndex), "Warning", new DoubleCollection { 4, 3 });

        for (var index = 0; index < points.Count; index++)
        {
            var item = _weeklyRemainingForecast[index];
            var point = points[index];
            var isToday = index == todayIndex;
            var marker = new Ellipse
            {
                Width = isToday ? 7 : 5,
                Height = isToday ? 7 : 5,
                Fill = ThemeBrush(item.IsProjected ? "Warning" : "Signal"),
                Stroke = isToday ? ThemeBrush("Panel") : null,
                StrokeThickness = isToday ? 1.5 : 0
            };
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            RemainingForecastCanvas.Children.Add(marker);

            var dayLabel = LocalizationService.IsEnglish
                ? item.Date.DayOfWeek.ToString()[..3]
                : "一二三四五六日"[index].ToString();
            AddChartText(dayLabel, point.X - (isToday ? 8 : 6), height - bottom + 5,
                isToday ? "Ink" : "Subtle", 10);
        }
        AddChartHoverTargets(RemainingForecastCanvas, points, _weeklyRemainingForecast
            .Select(item => LocalizationService.IsEnglish
                ? $"{item.Date.ToString("MMM d", CultureInfo.InvariantCulture)} · {item.RemainingPercent:0.##}% remaining" +
                  (item.IsProjected ? " (projected)" : string.Empty)
                : $"{item.Date:M月d日} · 剩余 {item.RemainingPercent:0.##}%" +
                  (item.IsProjected ? "（预测）" : string.Empty))
            .ToList(), 0, height);
    }

    private void AddForecastLine(IEnumerable<Point> source, string brushKey, DoubleCollection? dashArray)
    {
        var points = new PointCollection(source);
        if (points.Count < 2) return;
        RemainingForecastCanvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = ThemeBrush(brushKey),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = dashArray
        });
    }

    private void AddChartText(string text, double left, double top, string brushKey, double fontSize)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = ThemeBrush(brushKey),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = fontSize
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        RemainingForecastCanvas.Children.Add(label);
    }

    internal static bool TryReadPercent(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value is >= 1 and <= 99;

    internal static bool TryReadFloatingFontPercent(string? text, out double scale)
    {
        var valid = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) &&
                    percent is >= 100 and <= 150;
        scale = valid ? percent / 100d : FloatingFontScales.Default;
        return valid;
    }

    internal static string FormatTokens(double tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000:0.##}B",
        >= 1_000_000 => $"{tokens / 1_000_000:0.##}M",
        >= 1_000 => $"{tokens / 1_000:0.#}K",
        _ => $"{tokens:0}"
    };

    private SolidColorBrush ThemeBrush(string key) => (SolidColorBrush)FindResource(key);

    private void SetFooterStatus(string message, bool warning = false)
    {
        FooterStatusText.Text = message;
        FooterStatusText.Foreground = ThemeBrush(warning ? "WarningText" : "Subtle");
    }

    private static string L(string zh, string en) => LocalizationService.IsEnglish ? en : zh;
    private static string HighestUsage(double value) => LocalizationService.IsEnglish ? $"Highest usage {value:0.#}%" : $"最高窗口占用 {value:0.#}%";

    private static string BuildConnectionError(Exception ex) => ex switch
    {
        FileNotFoundException => L(
            "未找到 Codex CLI。可以安装 Codex，或通过 CODEX_EXECUTABLE 指定 codex.exe 的完整路径。",
            "Codex CLI was not found. Install Codex or set CODEX_EXECUTABLE to the full path of codex.exe."),
        Win32Exception { NativeErrorCode: 2 } => L("未找到 Codex CLI。可以安装 Codex，或通过 CODEX_EXECUTABLE 指定 codex.exe 的完整路径。", "Codex CLI was not found. Install Codex or set CODEX_EXECUTABLE to the full path of codex.exe."),
        Win32Exception => LocalizationService.IsEnglish ? $"Unable to start Codex CLI: {ex.Message}" : $"无法启动 Codex CLI：{ex.Message}",
        TimeoutException => LocalizationService.IsEnglish ? $"Codex App Server timed out: {ex.Message} Try Sync now again shortly." : $"Codex App Server 响应超时：{ex.Message} 请稍后点击“立即同步”重试。",
        _ when ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("login", StringComparison.OrdinalIgnoreCase) =>
            L("Codex 尚未登录。请先在 Codex CLI 或桌面应用完成登录，再点击“立即同步”。", "Codex is not signed in. Sign in using the CLI or desktop app, then select Sync now."),
        _ => LocalizationService.IsEnglish ? $"Unable to connect to Codex App Server: {ex.Message}" : $"无法连接 Codex App Server：{ex.Message}"
    };

    private void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        if (_service.LastSnapshot is { } snapshot) RenderSnapshot(snapshot);
        RenderResetSignals();
        LocalizationService.Apply(this);
        UpdateLanguageButton();
        UpdateThemeButton();
        UpdateDashboardChromeLanguage();
        UpdateDashboardPageLanguage();
    }
}
