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
    private readonly AiManagerService _service = new();
    private readonly DispatcherTimer _timer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshing;
    private bool _localGuardPaused;
    private bool _overrideCurrentBreach;
    private bool _alertedForCurrentBreach;
    private bool _checkingForUpdates;
    private UpdateCheckResult? _availableUpdate;
    private IReadOnlyList<AiRemainingForecastPoint> _weeklyRemainingForecast =
        Array.Empty<AiRemainingForecastPoint>();
    private IReadOnlyList<AiHourlyUsage> _todayHourlyUsage = Array.Empty<AiHourlyUsage>();
    private readonly Dictionary<string, DashboardCardState> _dashboardCards =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedDashboardCards =
        new(StringComparer.OrdinalIgnoreCase);
    private StackPanel _leftDashboardColumn = null!;
    private StackPanel _rightDashboardColumn = null!;
    private Point _cardDragStart;
    private Border? _draggedCard;
    private const double MinimumCardWidth = 280;
    private const double MinimumCardHeight = 88;
    private const double MaximumCardHeight = 1200;

    private static readonly string[] DefaultDashboardLayout =
    [
        "left:quota", "left:limits", "left:threads", "left:localUsage", "left:dailyUsage",
        "right:guard", "right:forecast", "right:settings", "right:sharedUsage"
    ];

    private sealed record DashboardCardState(
        string Id,
        string Title,
        Border Card,
        UIElement Body,
        TextBlock HeaderTitle,
        Button ToggleButton,
        Thumb ResizeThumb,
        double ExpandedMinHeight);

    public AiManagerWindow()
    {
        InitializeComponent();
        InitializeDashboardCards();
        _timer.Tick += async (_, _) => await RefreshAsync(showErrors: false);
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        LocalizationService.Apply(this);
        UpdateLanguageButton();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadPolicyControls();
        LoadSharedUsageControls();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(_service.Settings.RefreshSeconds, 30, 600));
        _timer.Start();
        _ = CheckForUpdatesAsync(showUpToDate: false);
        await RefreshAsync(showErrors: true);
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        _lifetime.Cancel();
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

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(showErrors: true);

    private void FloatingWindow_Click(object sender, RoutedEventArgs e) => AiFloatingWindow.ShowOrActivate();

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(showUpToDate: true);

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) =>
        await InstallUpdateAsync(confirm: true);

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
                _availableUpdate = null;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                if (showUpToDate)
                    WpfMessageBox.Show(this,
                        L("AIMaster 目前还没有已发布的 GitHub Release。",
                          "AIMaster does not have a published GitHub Release yet."),
                        L("检查更新", "Check for updates"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!result.UpdateAvailable)
            {
                _availableUpdate = null;
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                if (showUpToDate)
                    WpfMessageBox.Show(this,
                        L($"AIMaster 已是最新版（{result.CurrentVersion}）。", $"AIMaster is up to date ({result.CurrentVersion})."),
                        L("检查更新", "Check for updates"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _availableUpdate = result;
            InstallUpdateButton.Visibility = Visibility.Visible;
            FooterStatusText.Text = L($"发现 AIMaster {result.LatestVersion}，可立即更新。",
                $"AIMaster {result.LatestVersion} is available. Ready to update.");
            if (!showUpToDate)
            {
                var install = WpfMessageBox.Show(this,
                    L($"AIMaster {result.LatestVersion} 已发布（当前：{result.CurrentVersion}）。\n\n是否立即下载、安装并重启？",
                      $"AIMaster {result.LatestVersion} is available (current: {result.CurrentVersion}).\n\nDownload, install, and restart now?"),
                    L("发现新版本", "Update available"), MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (install == MessageBoxResult.Yes) await InstallUpdateAsync(confirm: false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (showUpToDate)
                WpfMessageBox.Show(this,
                    L($"无法检查更新：{ex.Message}", $"Unable to check for updates: {ex.Message}"),
                    L("检查更新", "Check for updates"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _checkingForUpdates = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task InstallUpdateAsync(bool confirm)
    {
        if (_availableUpdate is null) return;
        var update = _availableUpdate;
        if (confirm)
        {
            var confirmed = WpfMessageBox.Show(this,
                L($"现在下载并安装 AIMaster {update.LatestVersion}？\n\n程序将关闭并自动重启，本地设置会保留。",
                  $"Download and install AIMaster {update.LatestVersion} now?\n\nThe app will close and restart. Local settings will be preserved."),
                L("安装更新", "Install update"), MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (confirmed != MessageBoxResult.Yes) return;
        }

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

    private void InitializeDashboardCards()
    {
        var definitions = new[]
        {
            ("quota", "主额度跑道", QuotaCard),
            ("guard", "任务闸门", GuardCard),
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

        DashboardGrid.RowDefinitions.Clear();
        DashboardGrid.ColumnDefinitions.Clear();
        DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _leftDashboardColumn = CreateDashboardColumn();
        _rightDashboardColumn = CreateDashboardColumn();
        Grid.SetColumn(_rightDashboardColumn, 2);
        DashboardGrid.Children.Add(_leftDashboardColumn);
        DashboardGrid.Children.Add(_rightDashboardColumn);
        ApplyDashboardLayout();
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
        card.Margin = new Thickness(0, 0, 0, 8);
        card.MinWidth = MinimumCardWidth;
        card.ClipToBounds = true;
        card.AllowDrop = true;
        card.DragOver += DashboardCard_DragOver;
        card.Drop += DashboardCard_Drop;

        var frame = new Grid();
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid
        {
            Tag = id,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = L("拖动调整布局", "Drag to rearrange")
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.PreviewMouseLeftButtonDown += CardHeader_MouseLeftButtonDown;
        header.MouseMove += CardHeader_MouseMove;

        var headerTitle = new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("Label"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var grip = new TextBlock
        {
            Text = "⋮⋮",
            Foreground = Brush("#667881"),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Margin = new Thickness(8, 0, 7, 0)
        };
        Grid.SetColumn(grip, 1);
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
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            ToolTip = L("拖动自由调整卡片大小；双击恢复自适应", "Drag to resize; double-click to reset")
        };
        resizeThumb.DragStarted += CardResize_DragStarted;
        resizeThumb.DragDelta += CardResize_DragDelta;
        resizeThumb.DragCompleted += CardResize_DragCompleted;
        resizeThumb.PreviewMouseDoubleClick += CardResize_MouseDoubleClick;
        Grid.SetRowSpan(resizeThumb, 2);
        Panel.SetZIndex(resizeThumb, 2);
        header.Children.Add(headerTitle);
        header.Children.Add(grip);
        header.Children.Add(toggle);
        frame.Children.Add(header);
        Grid.SetRow(body, 1);
        frame.Children.Add(body);
        frame.Children.Add(resizeThumb);
        card.Child = frame;

        _dashboardCards[id] = new DashboardCardState(
            id, title, card, body, headerTitle, toggle, resizeThumb, card.MinHeight);
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
            var column = parts[0] == "right" ? _rightDashboardColumn : _leftDashboardColumn;
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

    private void CardHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || !_dashboardCards.TryGetValue(id, out var state)) return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        _cardDragStart = e.GetPosition(this);
        _draggedCard = state.Card;
    }

    private void CardHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedCard is null || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _cardDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _cardDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var card = _draggedCard;
        _draggedCard = null;
        card.Opacity = 0.6;
        DragDrop.DoDragDrop(card, card, DragDropEffects.Move);
        card.Opacity = 1;
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
        state.Card.Width = Math.Max(MinimumCardWidth, state.Card.ActualWidth);
        state.Card.Height = Math.Max(MinimumCardHeight, state.Card.ActualHeight);
        state.Card.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private void CardResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } ||
            !_dashboardCards.TryGetValue(id, out var state) || _collapsedDashboardCards.Contains(id)) return;
        var availableWidth = (state.Card.Parent as FrameworkElement)?.ActualWidth ?? state.Card.ActualWidth;
        var maximumWidth = Math.Max(MinimumCardWidth, availableWidth);
        state.Card.Width = Math.Clamp(state.Card.Width + e.HorizontalChange, MinimumCardWidth, maximumWidth);
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
        state.Card.Width = size.Width;
        state.Card.Height = size.Height;
        state.Card.HorizontalAlignment = HorizontalAlignment.Left;
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
        settings.DashboardCardLayout = CaptureDashboardColumn(_leftDashboardColumn, "left")
            .Concat(CaptureDashboardColumn(_rightDashboardColumn, "right"))
            .ToList();
        settings.CollapsedDashboardCards = _collapsedDashboardCards.OrderBy(id => id).ToList();
        _service.SaveSettings(settings);
    }

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
            state.ResizeThumb.ToolTip = L("拖动自由调整卡片大小；双击恢复自适应",
                "Drag to resize; double-click to reset");
        }
    }

    private async Task RefreshAsync(bool showErrors)
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        SyncStatusText.Text = L("正在读取 Codex…", "Reading Codex…");
        try
        {
            var snapshot = await _service.RefreshAsync(_lifetime.Token);
            ConnectionBanner.Visibility = Visibility.Collapsed;
            RenderSnapshot(snapshot);
            await ApplyGuardAsync(snapshot);
            SyncStatusText.Text = L("已连接", "Connected");
            LastSyncText.Text = $"SYNC {snapshot.CapturedAt.LocalDateTime:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            var message = BuildConnectionError(ex);
            ConnectionText.Text = message;
            ConnectionBanner.Visibility = Visibility.Visible;
            SyncStatusText.Text = L("连接失败", "Connection failed");
            FooterStatusText.Text = ex is TimeoutException
                ? L("Codex 当前响应较慢，请稍后点击“立即同步”重试。", "Codex is responding slowly. Try Sync now again shortly.")
                : L("请确认 Codex 已安装并登录，然后重试。", "Make sure Codex is installed and signed in, then try again.");
            if (showErrors)
                WpfMessageBox.Show(this, message, L("无法读取 Codex 用量", "Unable to read Codex usage"), MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var forecast = _service.BuildForecast(snapshot);
        ForecastSummaryText.Text = forecast.Summary;
        ForecastSummaryText.Foreground = Brush(forecast.ExhaustsBeforeReset ? "#FFB36A" : "#F1F5F2");
        DailyTokensText.Text = FormatTokens(forecast.DailyTokens);
        DailyPercentText.Text = forecast.DailyPercent > 0 ? $"{forecast.DailyPercent:0.#}%" : "--";
        _weeklyRemainingForecast = AiManagerService.BuildWeeklyRemainingForecast(snapshot, forecast);
        RenderRemainingForecastChart();
        RenderUsageBars(snapshot.DailyUsage);
        var weeklyLimit = snapshot.Limits
            .Where(item => item.WindowDurationMinutes is >= 6 * 24 * 60 and <= 8 * 24 * 60)
            .OrderBy(item => Math.Abs(item.WindowDurationMinutes - 7 * 24 * 60))
            .FirstOrDefault();
        RenderLocalUsage(snapshot.LocalUsage, snapshot.DailyUsage, weeklyLimit?.UsedPercent);
        RenderSharedUsage(snapshot.SharedUsage);
        FooterStatusText.Text = snapshot.LifetimeTokens is { } lifetime
            ? (LocalizationService.IsEnglish ? $"Lifetime {FormatTokens(lifetime)} tokens · From Codex App Server" : $"累计 {FormatTokens(lifetime)} tokens · 数据来自 Codex App Server")
            : L("数据来自 Codex App Server；认证由 Codex 管理。", "Data comes from Codex App Server; authentication is managed by Codex.");
        LocalizationService.Apply(this);
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
        WpfMessageBox.Show(this,
            LocalizationService.IsEnglish
                ? $"Usage has reached {highest:0.#}%, above the {_service.Settings.PausePercent:0.#}% pause limit.\n\n" +
                  $"Interrupted {interrupted} running task(s) visible to this App Server connection and paused the guard. " +
                  "Tasks in other Codex windows may need to be stopped manually."
                : $"用量已达到 {highest:0.#}%，超过暂停线 {_service.Settings.PausePercent:0.#}%。\n\n" +
                  $"已中断当前 App Server 可见的 {interrupted} 个运行任务，并将保护状态设为暂停。" +
                  "其他 Codex 窗口的任务可能需要手动停止。",
            L("AIMaster 已暂停任务", "AIMaster paused the task guard"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ApplyQuotaVisual(double used)
    {
        if (used >= _service.Settings.PausePercent)
        {
            QuotaStateText.Text = L("暂停线", "Pause limit");
            QuotaStateText.Foreground = Brush("#FFB4B8");
            QuotaStateBadge.Background = Brush("#3A2528");
            MainUsageBar.Foreground = Brush("#E45B65");
        }
        else if (used >= _service.Settings.WarningPercent)
        {
            QuotaStateText.Text = L("接近限制", "Near limit");
            QuotaStateText.Foreground = Brush("#F2CD7D");
            QuotaStateBadge.Background = Brush("#382F1D");
            MainUsageBar.Foreground = Brush("#E6A846");
        }
        else
        {
            QuotaStateText.Text = L("额度安全", "Quota safe");
            QuotaStateText.Foreground = Brush("#68E0B2");
            QuotaStateBadge.Background = Brush("#18382D");
            MainUsageBar.Foreground = Brush("#22C58B");
        }
    }

    private void SetGuardVisual(bool paused, string status, string detail)
    {
        _localGuardPaused = paused;
        GuardGlyph.Text = paused ? "HOLD" : "GO";
        GuardGlyph.Foreground = Brush(paused ? "#E45B65" : "#22C58B");
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
            WpfMessageBox.Show(this,
                LocalizationService.IsEnglish
                    ? $"Interrupted {interrupted} running task(s) visible to this App Server connection.\n" +
                      "Tasks in other Codex windows do not share live state and may need to be stopped manually."
                    : $"已中断当前 App Server 可见的 {interrupted} 个运行任务。\n其他 Codex 窗口中的任务不共享运行态，可能需要手动停止。",
                L("暂停完成", "Pause completed"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, BuildConnectionError(ex), L("暂停失败", "Unable to pause tasks"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            WpfMessageBox.Show(this,
                L("请输入 1–99 之间的预警百分比、100–150 之间的浮窗字号，并确保预警线低于暂停线。",
                    "Enter guard percentages from 1 to 99, a floating font size from 100 to 150, and keep the warning limit below the pause limit."),
                L("策略无效", "Invalid guard policy"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = _service.ReloadSettings();
        settings.WarningPercent = warning;
        settings.PausePercent = pause;
        settings.AutoPause = AutoPauseCheck.IsChecked == true;
        settings.FloatingFontScale = floatingFontScale;
        _service.SaveSettings(settings);
        AiFloatingWindow.ApplySavedFontScale(floatingFontScale);
        FooterStatusText.Text = L("设置已保存并应用", "Settings saved and applied");
        if (_service.LastSnapshot is { } snapshot)
        {
            ApplyQuotaVisual(snapshot.MainLimit?.UsedPercent ?? 0);
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
    }

    private void LoadSharedUsageControls()
    {
        SharedUsageEnabledCheck.IsChecked = _service.Settings.SharedUsageEnabled;
        SharedUsageServerUrlBox.Text = _service.Settings.SharedUsageServerUrl;
        SharedUsageDeviceNameBox.Text = _service.Settings.SharedUsageDeviceName;
        SharedUsageSyncKeyBox.Password = _service.Settings.SharedUsageSyncKey;
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
            WpfMessageBox.Show(this, error, L("共享设置无效", "Invalid sharing settings"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SharedUsageSaveButton.IsEnabled = false;
        try
        {
            var settings = _service.ReloadSettings();
            settings.SharedUsageEnabled = enabled;
            settings.SharedUsageServerUrl = serverUrl;
            settings.SharedUsageDeviceName = deviceName;
            settings.SharedUsageSyncKey = syncKey;
            _service.SaveSettings(settings);
            if (!enabled)
            {
                RenderSharedUsage(new AiSharedUsageSummary { Enabled = false });
                SharedUsageStatusText.Text = L("共享统计已关闭", "Shared usage is disabled");
                return;
            }

            SharedUsageStatusText.Text = L("正在同步设备数据…", "Syncing device usage…");
            if (_service.LastSnapshot is null)
            {
                await RefreshAsync(showErrors: true);
                return;
            }
            var localUsage = _service.LastSnapshot.LocalUsage;
            var sharedUsage = await _service.SyncSharedUsageAsync(localUsage, _lifetime.Token);
            RenderSharedUsage(sharedUsage);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            SharedUsageSaveButton.IsEnabled = true;
        }
    }

    private void RenderSharedUsage(AiSharedUsageSummary usage)
    {
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
            var column = new StackPanel { Width = 34, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Bottom };
            column.Children.Add(new Border
            {
                Height = 8 + 54d * day.Tokens / max,
                Background = Brush(day.Date == DateOnly.FromDateTime(DateTime.Today) ? "#22C58B" : "#334B50"),
                CornerRadius = new CornerRadius(3),
                ToolTip = LocalizationService.IsEnglish
                    ? $"{day.Date.ToString("MMM d", CultureInfo.InvariantCulture)} · {FormatTokens(day.Tokens)} tokens"
                    : $"{day.Date:M月d日} · {FormatTokens(day.Tokens)} tokens"
            });
            column.Children.Add(new TextBlock
            {
                Text = day.Date.Day.ToString(CultureInfo.InvariantCulture),
                Foreground = Brush("#71838A"),
                FontFamily = new FontFamily("Cascadia Mono"),
                FontSize = 9,
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
        const double left = 34;
        const double top = 8;
        const double right = 8;
        const double bottom = 20;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        foreach (var fraction in new[] { 0d, 0.5d, 1d })
        {
            var y = top + plotHeight * (1 - fraction);
            DailyUsageCanvas.Children.Add(new Line
            {
                X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y,
                Stroke = Brush("#293640"), StrokeThickness = 1
            });
        }

        AddCanvasText(DailyUsageCanvas, FormatTokens(maximum), 0, top - 5, "#71838A", 8);
        AddCanvasText(DailyUsageCanvas, "0", 18, top + plotHeight - 6, "#71838A", 8);
        var points = Enumerable.Range(0, 24)
            .Select(hour => new Point(
                left + plotWidth * hour / 23d,
                top + plotHeight * (1 - byHour.GetValueOrDefault(hour) / (double)maximum)))
            .ToList();
        DailyUsageCanvas.Children.Add(new Polyline
        {
            Points = new PointCollection(points),
            Stroke = Brush("#22C58B"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });

        foreach (var hour in new[] { 0, 6, 12, 18, 23 })
            AddCanvasText(DailyUsageCanvas, $"{hour:00}", points[hour].X - 6,
                height - bottom + 4, "#71838A", 8);
        foreach (var item in _todayHourlyUsage.Where(item => item.Tokens > 0))
        {
            var point = points[Math.Clamp(item.Hour, 0, 23)];
            var marker = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = Brush("#22C58B"),
                Stroke = Brush("#F1F5F2"),
                StrokeThickness = 1,
                ToolTip = LocalizationService.IsEnglish
                    ? $"{item.Hour:00}:00–{item.Hour:00}:59 · {FormatTokens(item.Tokens)} tokens"
                    : $"{item.Hour:00}:00–{item.Hour:00}:59 · {FormatTokens(item.Tokens)} Token"
            };
            Canvas.SetLeft(marker, point.X - 3);
            Canvas.SetTop(marker, point.Y - 3);
            DailyUsageCanvas.Children.Add(marker);
        }
    }

    private static void AddCanvasText(
        Canvas canvas, string text, double left, double top, string color, double fontSize)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = Brush(color),
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

        const double left = 27;
        const double top = 7;
        const double right = 6;
        const double bottom = 20;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        foreach (var percent in new[] { 100d, 50d, 0d })
        {
            var y = top + (100 - percent) / 100 * plotHeight;
            RemainingForecastCanvas.Children.Add(new Line
            {
                X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y,
                Stroke = Brush("#293640"), StrokeThickness = 1
            });
            AddChartText($"{percent:0}", 0, y - 6, "#71838A", 8);
        }

        var points = _weeklyRemainingForecast
            .Select((item, index) => new Point(
                left + plotWidth * index / Math.Max(1, _weeklyRemainingForecast.Count - 1),
                top + (100 - item.RemainingPercent) / 100 * plotHeight))
            .ToList();
        var todayIndex = _weeklyRemainingForecast.TakeWhile(item => !item.IsProjected).Count() - 1;
        todayIndex = Math.Clamp(todayIndex, 0, points.Count - 1);

        AddForecastLine(points.Take(todayIndex + 1), "#22C58B", null);
        AddForecastLine(points.Skip(todayIndex), "#FFB36A", new DoubleCollection { 4, 3 });

        for (var index = 0; index < points.Count; index++)
        {
            var item = _weeklyRemainingForecast[index];
            var point = points[index];
            var isToday = index == todayIndex;
            var marker = new Ellipse
            {
                Width = isToday ? 7 : 5,
                Height = isToday ? 7 : 5,
                Fill = Brush(item.IsProjected ? "#FFB36A" : "#22C58B"),
                Stroke = isToday ? Brush("#F1F5F2") : null,
                StrokeThickness = isToday ? 1.5 : 0,
                ToolTip = LocalizationService.IsEnglish
                    ? $"{item.Date.ToString("MMM d", CultureInfo.InvariantCulture)} · {item.RemainingPercent:0.#}% remaining"
                    : $"{item.Date:M月d日} · 剩余 {item.RemainingPercent:0.#}%"
            };
            Canvas.SetLeft(marker, point.X - marker.Width / 2);
            Canvas.SetTop(marker, point.Y - marker.Height / 2);
            RemainingForecastCanvas.Children.Add(marker);

            var dayLabel = LocalizationService.IsEnglish
                ? item.Date.DayOfWeek.ToString()[..3]
                : "一二三四五六日"[index].ToString();
            AddChartText(dayLabel, point.X - (isToday ? 7 : 5), height - bottom + 4,
                isToday ? "#F1F5F2" : "#71838A", isToday ? 9 : 8);
        }
    }

    private void AddForecastLine(IEnumerable<Point> source, string color, DoubleCollection? dashArray)
    {
        var points = new PointCollection(source);
        if (points.Count < 2) return;
        RemainingForecastCanvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = Brush(color),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = dashArray
        });
    }

    private void AddChartText(string text, double left, double top, string color, double fontSize)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = Brush(color),
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

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

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
        LocalizationService.Apply(this);
        UpdateLanguageButton();
        UpdateDashboardChromeLanguage();
    }
}
