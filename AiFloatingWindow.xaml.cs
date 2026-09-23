using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AIMaster.Models;
using AIMaster.Services;

namespace AIMaster;

public partial class AiFloatingWindow : Window
{
    private static AiFloatingWindow? _instance;
    private readonly AiManagerService _service = new();
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _dockHideTimer = new() { Interval = TimeSpan.FromMilliseconds(820) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshing;
    private AiFloatingSnapshot? _lastSnapshot;
    private DockEdge _dockEdge;
    private bool _isDockCollapsed;
    private double _expandedLeft;
    private double _expandedTop;
    private IReadOnlyList<AiThreadSummary> _displayTasks = Array.Empty<AiThreadSummary>();
    private int _visibleTaskCount = -1;
    private bool _hasSizedForFirstSnapshot;
    private bool _autoCollapse;
    private double _fontScale;
    private string _theme = ThemeModes.Dark;
    private Rect _dockWorkArea = Rect.Empty;

    private const double DockThreshold = 30;
    private const double PeekSize = 8;
    private const double BaseWindowHeight = 98;
    private const double SecondaryTaskRowHeight = 18;

    private enum DockEdge { None, Left, Right, Top }

    public AiFloatingWindow()
    {
        InitializeComponent();
        ApplyTheme(_service.Settings.Theme);
        _autoCollapse = _service.Settings.FloatingAutoCollapse;
        ApplyFontScale(_service.Settings.FloatingFontScale);
        ApplyRefreshInterval(_service.Settings.FloatingRefreshSeconds);
        _timer.Tick += async (_, _) =>
        {
            if (!_isDockCollapsed) await RefreshAsync();
        };
        _dockHideTimer.Tick += (_, _) =>
        {
            _dockHideTimer.Stop();
            if (_autoCollapse && _dockEdge != DockEdge.None && !IsMouseOver) CollapseDocked();
        };
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        LocalizationService.Apply(this);
        UpdateContextMenu();
    }

    public static void ShowOrActivate()
    {
        if (_instance is { IsVisible: true })
        {
            _instance.Activate();
            return;
        }
        _instance = new AiFloatingWindow();
        _instance.Show();
    }

    internal static void ApplySavedFontScale(double scale) => _instance?.ApplyFontScale(scale);

    internal static void ApplySavedTheme(string theme) => _instance?.ApplyTheme(theme);

    private void ApplyTheme(string? theme)
    {
        _theme = ThemeModes.Normalize(theme);
        ThemeService.Apply(Resources, _theme);
        if (_lastSnapshot is { } snapshot) RenderSnapshot(snapshot);
    }

    private void ApplyFontScale(double scale)
    {
        _fontScale = FloatingFontScales.Normalize(scale);
        Resources["FloatFontButton"] = 9d * _fontScale;
        Resources["FloatFontBrand"] = 7.5d * _fontScale;
        Resources["FloatFontSmall"] = 7d * _fontScale;
        Resources["FloatFontTask"] = 10d * _fontScale;
        Resources["FloatFontQuota"] = 14d * _fontScale;
        Resources["FloatFontPercent"] = 8d * _fontScale;
        Resources["FloatFontModel"] = 9d * _fontScale;

        if (_lastSnapshot is { } snapshot) RenderSnapshot(snapshot);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!RestorePlacement())
        {
            var workArea = GetCurrentMonitorWorkArea();
            Left = workArea.Right - Width - 22;
            Top = workArea.Bottom - Height - 22;
        }
        _timer.Start();
        await RefreshAsync();
    }

    private bool RestorePlacement()
    {
        var saved = _service.Settings.FloatingWindowPlacement;
        if (saved is not { IsValid: true }) return false;

        Left = saved.Left;
        Top = saved.Top;
        var placement = FitPlacementToWorkArea(saved, GetCurrentMonitorWorkArea(), MinWidth, MinHeight);
        if (placement is null) return false;

        Width = placement.Value.Width;
        Height = placement.Value.Height;
        Left = placement.Value.Left;
        Top = placement.Value.Top;
        _hasSizedForFirstSnapshot = true;
        DetectDockAfterDrag();
        return true;
    }

    internal static Rect? FitPlacementToWorkArea(AiFloatingWindowPlacement? saved, Rect workArea,
        double minWidth, double minHeight)
    {
        if (saved is not { IsValid: true } || workArea.IsEmpty ||
            !double.IsFinite(workArea.Left) || !double.IsFinite(workArea.Top) ||
            !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height) ||
            workArea.Width <= 0 || workArea.Height <= 0) return null;

        var width = Math.Clamp(saved.Width, minWidth, Math.Max(minWidth, workArea.Width));
        var height = Math.Clamp(saved.Height, minHeight, Math.Max(minHeight, workArea.Height));
        var left = Math.Clamp(saved.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        var top = Math.Clamp(saved.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return new Rect(left, top, width, height);
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _dockHideTimer.Stop();
        SavePlacement();
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        _lifetime.Cancel();
        await _service.DisposeAsync();
        _lifetime.Dispose();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var snapshot = await _service.RefreshFloatingAsync(_lifetime.Token);
            RenderSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RenderError(ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RenderSnapshot(AiFloatingSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        var taskName = LocalizationService.LocalizeKnownText(snapshot.TaskName);
        TaskNameText.Text = taskName;
        TaskNameText.ToolTip = taskName;
        TaskStatusText.Text = LocalizedStatus(snapshot.TaskStatus);
        RemainingQuotaText.Text = snapshot.RemainingPercent?.ToString("0.#", CultureInfo.InvariantCulture) ?? "--";
        RemainingQuotaText.ToolTip = snapshot.ResetsAt is { } reset
            ? LocalizationService.IsEnglish
                ? $"{snapshot.QuotaWindow}\nResets {reset.LocalDateTime.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)}"
                : $"{snapshot.QuotaWindow}\n{reset.LocalDateTime:M月d日 HH:mm} 重置"
            : snapshot.QuotaWindow;
        var modelName = LocalizationService.LocalizeKnownText(snapshot.ModelName);
        ModelText.Text = modelName;
        ModelText.ToolTip = modelName;
        ReasoningText.Text = string.IsNullOrWhiteSpace(snapshot.ReasoningEffort)
            ? "--"
            : snapshot.ReasoningEffort.ToUpperInvariant();
        SyncAgeText.Text = snapshot.CapturedAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var tasks = snapshot.Tasks.Count > 0
            ? snapshot.Tasks
            : new List<AiThreadSummary>
            {
                new() { Title = snapshot.TaskName, Status = snapshot.TaskStatus, ModelName = snapshot.ModelName,
                    ReasoningEffort = snapshot.ReasoningEffort }
            };
        _displayTasks = tasks;
        PrimaryTaskRow.Tag = tasks[0].Id;
        var primaryHasLink = BuildTaskUri(tasks[0].Id) is not null;
        PrimaryTaskRow.Cursor = primaryHasLink ? Cursors.Hand : Cursors.Arrow;
        PrimaryTaskRow.Style = primaryHasLink ? (Style)FindResource("TaskHoverRow") : null;
        if (!_hasSizedForFirstSnapshot)
        {
            _hasSizedForFirstSnapshot = true;
            Height = BaseWindowHeight + Math.Clamp(tasks.Count - 1, 0, 2) * SecondaryTaskRowHeight;
        }
        RefreshVisibleTaskRows(force: true);
        ApplyStatusVisual(snapshot.TaskStatus);
    }

    private void RefreshVisibleTaskRows(bool force = false)
    {
        if (SecondaryTasksPanel is null) return;
        var availableHeight = ActualHeight > 0 ? ActualHeight : Height;
        var visibleTaskCount = CalculateVisibleTaskCount(availableHeight, _displayTasks.Count);
        if (!force && visibleTaskCount == _visibleTaskCount) return;
        _visibleTaskCount = visibleTaskCount;

        SecondaryTasksPanel.Children.Clear();
        foreach (var task in _displayTasks.Skip(1).Take(Math.Max(0, visibleTaskCount - 1)))
        {
            var color = ThemeBrush(StatusBrushKey(task.Status));
            var hasLink = BuildTaskUri(task.Id) is not null;
            var row = new Grid { Height = 18, Tag = task.Id,
                Style = hasLink ? (Style)FindResource("TaskHoverRow") : null,
                Cursor = hasLink ? Cursors.Hand : Cursors.Arrow };
            row.MouseLeftButtonDown += TaskRow_MouseLeftButtonDown;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Ellipse { Width = 4, Height = 4, Fill = color, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock
            {
                Text = task.DisplayTitle, Foreground = ThemeBrush("FloatingSecondaryInk"), FontSize = 8.5 * _fontScale,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 5, 0)
            };
            Grid.SetColumn(title, 1);
            var status = new TextBlock
            {
                Text = LocalizedStatus(task.Status), Foreground = color, FontSize = 6.5 * _fontScale,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(status, 2);
            row.Children.Add(dot);
            row.Children.Add(title);
            row.Children.Add(status);
            row.ToolTip = $"{task.DisplayTitle}\n{LocalizedStatus(task.Status)} · {task.ModelName ?? "--"} {task.ReasoningEffort?.ToUpperInvariant()}";
            SecondaryTasksPanel.Children.Add(row);
        }
    }

    internal static int CalculateVisibleTaskCount(double windowHeight, int taskCount)
    {
        if (taskCount <= 0) return 0;
        var extraRows = (int)Math.Floor(Math.Max(0, windowHeight - BaseWindowHeight) / SecondaryTaskRowHeight);
        return Math.Min(taskCount, 1 + extraRows);
    }

    internal static Uri? BuildTaskUri(string? threadId) => Guid.TryParse(threadId, out var id)
        ? new Uri($"codex://threads/{id:D}")
        : null;

    private void TaskRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement row || BuildTaskUri(row.Tag as string) is not { } uri) return;
        e.Handled = true;
        _dockHideTimer.Stop();
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                LocalizationService.IsEnglish ? $"Could not open the Codex task: {ex.Message}" : $"无法打开 Codex 任务：{ex.Message}",
                LocalizationService.IsEnglish ? "Open task" : "打开任务",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshVisibleTaskRows();

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _hasSizedForFirstSnapshot = true;
        _dockHideTimer.Stop();
        StopPositionAnimations();
        _isDockCollapsed = false;
        _dockEdge = DockEdge.None;
        _dockWorkArea = Rect.Empty;
        PeekHandle.Visibility = Visibility.Collapsed;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string direction }) return;

        var width = ActualWidth;
        var height = ActualHeight;
        var left = Left;
        var top = Top;
        var maxWidth = double.IsInfinity(MaxWidth) ? double.MaxValue : MaxWidth;
        var maxHeight = double.IsInfinity(MaxHeight) ? double.MaxValue : MaxHeight;

        if (direction.Contains("Left", StringComparison.Ordinal))
        {
            var targetWidth = Math.Clamp(width - e.HorizontalChange, MinWidth, maxWidth);
            left += width - targetWidth;
            width = targetWidth;
        }
        else if (direction.Contains("Right", StringComparison.Ordinal))
        {
            width = Math.Clamp(width + e.HorizontalChange, MinWidth, maxWidth);
        }

        if (direction.Contains("Top", StringComparison.Ordinal))
        {
            var targetHeight = Math.Clamp(height - e.VerticalChange, MinHeight, maxHeight);
            top += height - targetHeight;
            height = targetHeight;
        }
        else if (direction.Contains("Bottom", StringComparison.Ordinal))
        {
            height = Math.Clamp(height + e.VerticalChange, MinHeight, maxHeight);
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var workArea = GetCurrentMonitorWorkArea();
        Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        DetectDockAfterDrag();
        SavePlacement();
    }

    private void RenderError(Exception ex)
    {
        TaskStatusText.Text = LocalizationService.IsEnglish ? "Connection issue" : "连接异常";
        TaskNameText.Text = ex is TimeoutException
            ? (LocalizationService.IsEnglish ? "Codex is responding slowly; retrying automatically" : "Codex 响应较慢，将自动重试")
            : (LocalizationService.IsEnglish ? "Codex status is temporarily unavailable" : "暂时无法读取 Codex 状态");
        TaskNameText.ToolTip = ex.Message;
        SyncAgeText.Text = "RETRY";
        ApplyStatusVisual("systemError");
    }

    private void ApplyStatusVisual(string status)
    {
        var colorKey = StatusBrushKey(status);
        var badgeKey = status switch
        {
            "active" or "inProgress" => "SignalSoft",
            "waitingOnApproval" or "waitingOnUserInput" => "WarningSoft",
            "failed" or "systemError" => "DangerSoft",
            "interrupted" => "InterruptedSoft",
            _ => "IdleSoft"
        };
        StatusDot.Fill = ThemeBrush(colorKey);
        PeekSignal.Fill = ThemeBrush(colorKey);
        TaskStatusText.Foreground = ThemeBrush(colorKey);
        TaskStatusBadge.Background = ThemeBrush(badgeKey);
    }

    private static string StatusBrushKey(string status) => status switch
    {
        "active" or "inProgress" => "Signal",
        "waitingOnApproval" or "waitingOnUserInput" => "Warning",
        "failed" or "systemError" => "Danger",
        "interrupted" => "Interrupted",
        _ => "Idle"
    };

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            _dockHideTimer.Stop();
            StopPositionAnimations();
            _isDockCollapsed = false;
            // 拖动期间先解除吸附，避免鼠标离开旧边缘时触发延迟收起。
            _dockEdge = DockEdge.None;
            PeekHandle.Visibility = Visibility.Collapsed;
            DragMove();
            DetectDockAfterDrag();
            SavePlacement();
        }
    }

    private void SavePlacement()
    {
        var saved = new AiFloatingWindowPlacement
        {
            Left = _dockEdge == DockEdge.None ? Left : _expandedLeft,
            Top = _dockEdge == DockEdge.None ? Top : _expandedTop,
            Width = ActualWidth > 0 ? ActualWidth : Width,
            Height = ActualHeight > 0 ? ActualHeight : Height
        };
        if (!saved.IsValid) return;

        try
        {
            var settings = _service.ReloadSettings();
            settings.FloatingWindowPlacement = saved;
            _service.SaveSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[AiFloatingWindow] Could not save placement: {ex.Message}");
        }
    }

    private void DetectDockAfterDrag()
    {
        var workArea = GetCurrentMonitorWorkArea();
        var distances = new (DockEdge Edge, double Distance)[]
        {
            (DockEdge.Left, Math.Abs(Left - workArea.Left)),
            (DockEdge.Right, Math.Abs(Left + Width - workArea.Right)),
            (DockEdge.Top, Math.Abs(Top - workArea.Top))
        };
        var nearest = distances.OrderBy(item => item.Distance).First();
        if (nearest.Distance > DockThreshold)
        {
            _dockEdge = DockEdge.None;
            _dockWorkArea = Rect.Empty;
            PeekHandle.Visibility = Visibility.Collapsed;
            return;
        }

        _dockEdge = nearest.Edge;
        _dockWorkArea = workArea;
        _expandedLeft = _dockEdge switch
        {
            DockEdge.Left => workArea.Left,
            DockEdge.Right => workArea.Right - Width,
            _ => Math.Clamp(Left, workArea.Left, workArea.Right - Width)
        };
        _expandedTop = _dockEdge == DockEdge.Top
            ? workArea.Top
            : Math.Clamp(Top, workArea.Top, workArea.Bottom - Height);
        ConfigurePeekHandle();
        AnimatePosition(_expandedLeft, _expandedTop, 130);
    }

    private void CollapseDocked(bool animate = true)
    {
        if (_dockEdge == DockEdge.None || _isDockCollapsed) return;
        var workArea = GetDockWorkArea();
        var targetLeft = _dockEdge switch
        {
            DockEdge.Left => workArea.Left - Width + PeekSize,
            DockEdge.Right => workArea.Right - PeekSize,
            _ => _expandedLeft
        };
        var targetTop = _dockEdge == DockEdge.Top
            ? workArea.Top - Height + PeekSize
            : _expandedTop;

        _isDockCollapsed = true;
        _timer.Stop();
        ConfigurePeekHandle();
        PeekHandle.Visibility = Visibility.Visible;
        if (animate)
        {
            AnimatePosition(targetLeft, targetTop, 180, HideDockedShell);
        }
        else
        {
            SetPosition(targetLeft, targetTop);
            HideDockedShell();
        }
    }

    private void ExpandDocked(bool animate = true)
    {
        if (_dockEdge == DockEdge.None || !_isDockCollapsed) return;
        _isDockCollapsed = false;
        Shell.Opacity = 1;
        Shell.IsHitTestVisible = true;
        if (IsLoaded)
        {
            _timer.Start();
            _ = RefreshAsync();
        }
        if (animate)
        {
            AnimatePosition(_expandedLeft, _expandedTop, 180, () => PeekHandle.Visibility = Visibility.Collapsed);
        }
        else
        {
            SetPosition(_expandedLeft, _expandedTop);
            PeekHandle.Visibility = Visibility.Collapsed;
        }
    }

    private void ConfigurePeekHandle()
    {
        PeekHandle.ToolTip = LocalizationService.IsEnglish ? "Hover to expand" : "悬停展开";
        PeekHandle.Width = _dockEdge == DockEdge.Top ? 56 : PeekSize;
        PeekHandle.Height = _dockEdge == DockEdge.Top ? PeekSize : 56;
        PeekSignal.Width = _dockEdge == DockEdge.Top ? 34 : 2;
        PeekSignal.Height = _dockEdge == DockEdge.Top ? 2 : 34;
        PeekHandle.HorizontalAlignment = _dockEdge switch
        {
            DockEdge.Left => HorizontalAlignment.Right,
            DockEdge.Right => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Center
        };
        PeekHandle.VerticalAlignment = _dockEdge == DockEdge.Top
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Center;
        PeekHandle.CornerRadius = _dockEdge switch
        {
            DockEdge.Left => new CornerRadius(0, 6, 6, 0),
            DockEdge.Top => new CornerRadius(0, 0, 5, 5),
            _ => new CornerRadius(6, 0, 0, 6)
        };
    }

    private void HideDockedShell()
    {
        Shell.Opacity = 0;
        Shell.IsHitTestVisible = false;
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _dockHideTimer.Stop();
        if (_isDockCollapsed) ExpandDocked();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_autoCollapse || _dockEdge == DockEdge.None || _isDockCollapsed) return;
        _dockHideTimer.Stop();
        _dockHideTimer.Start();
    }

    private void PeekHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dockHideTimer.Stop();
        ExpandDocked();
        e.Handled = true;
    }

    private void AnimatePosition(double targetLeft, double targetTop, int milliseconds, Action? completed = null)
    {
        StopPositionAnimations();
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var leftAnimation = new DoubleAnimation(Left, targetLeft, duration) { EasingFunction = easing };
        var topAnimation = new DoubleAnimation(Top, targetTop, duration) { EasingFunction = easing };
        leftAnimation.Completed += (_, _) =>
        {
            SetPosition(targetLeft, targetTop);
            completed?.Invoke();
        };
        BeginAnimation(LeftProperty, leftAnimation);
        BeginAnimation(TopProperty, topAnimation);
    }

    private void StopPositionAnimations()
    {
        var currentLeft = Left;
        var currentTop = Top;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = currentLeft;
        Top = currentTop;
    }

    private void SetPosition(double left, double top)
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = left;
        Top = top;
    }

    private Rect GetDockWorkArea() => _dockWorkArea.IsEmpty
        ? GetCurrentMonitorWorkArea()
        : _dockWorkArea;

    private Rect GetCurrentMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var windowRect))
            return SystemParameters.WorkArea;

        var nativeArea = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1d : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1d : dpi.DpiScaleY;

        // Screen.WorkingArea uses virtual-desktop pixels while WPF positions use DIPs.
        // Anchor the conversion at this window's native rectangle so negative monitor
        // coordinates and mixed-DPI secondary displays remain in the same coordinate space.
        var left = Left + (nativeArea.Left - windowRect.Left) / scaleX;
        var top = Top + (nativeArea.Top - windowRect.Top) / scaleY;
        return new Rect(left, top, nativeArea.Width / scaleX, nativeArea.Height / scaleY);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Content = Topmost ? "●" : "○";
        PinButton.ToolTip = LocalizationService.T(Topmost ? "取消置顶" : "保持置顶");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void RefreshNow_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CollapseToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_dockEdge == DockEdge.None) return;
        if (_isDockCollapsed) ExpandDocked();
        else CollapseDocked();
    }

    private void AutoCollapseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _autoCollapse = AutoCollapseMenuItem.IsChecked;
        var settings = _service.ReloadSettings();
        settings.FloatingAutoCollapse = _autoCollapse;
        _service.SaveSettings(settings);
        if (!_autoCollapse)
        {
            _dockHideTimer.Stop();
            if (_isDockCollapsed) ExpandDocked();
        }
    }

    private void RefreshFrequency_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || !int.TryParse(item.Tag?.ToString(), out var seconds)) return;
        ApplyRefreshInterval(seconds);
        var settings = _service.ReloadSettings();
        settings.FloatingRefreshSeconds = seconds;
        _service.SaveSettings(settings);
        UpdateContextMenu();
    }

    private void ApplyRefreshInterval(int seconds) =>
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30));

    private async void TaskNameSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var settings = _service.ReloadSettings();
        settings.TaskNameSource = TaskNameSources.Normalize(item.Tag?.ToString());
        _service.SaveSettings(settings);
        UpdateContextMenu();
        await RefreshAsync();
    }

    private void FloatingMenu_Opened(object sender, RoutedEventArgs e) => UpdateContextMenu();

    private void UpdateContextMenu()
    {
        var english = LocalizationService.IsEnglish;
        RefreshNowMenuItem.Header = english ? "Refresh now" : "立即刷新";
        AutoCollapseMenuItem.Header = english ? "Auto-hide at edge" : "靠边自动收起";
        AutoCollapseMenuItem.IsChecked = _autoCollapse;
        CollapseToggleMenuItem.Header = _isDockCollapsed
            ? (english ? "Expand window" : "展开浮窗")
            : (english ? "Collapse now" : "立即收起");
        CollapseToggleMenuItem.IsEnabled = _dockEdge != DockEdge.None;
        TaskNameSourceMenuItem.Header = english ? "Task name" : "任务名称显示";
        ConversationTitleMenuItem.Header = english ? "Conversation title" : "对话标题";
        LatestUserMessageMenuItem.Header = english ? "Latest sent message" : "最后发送内容";
        var taskNameSource = TaskNameSources.Normalize(_service.Settings.TaskNameSource);
        ConversationTitleMenuItem.IsChecked = taskNameSource == TaskNameSources.ConversationTitle;
        LatestUserMessageMenuItem.IsChecked = taskNameSource == TaskNameSources.LatestUserMessage;
        RefreshFrequencyMenuItem.Header = english ? "Refresh interval" : "刷新频率";
        foreach (var item in RefreshFrequencyMenuItem.Items.OfType<MenuItem>())
        {
            if (!int.TryParse(item.Tag?.ToString(), out var seconds)) continue;
            item.Header = english ? $"{seconds} sec" : $"{seconds} 秒";
            item.IsChecked = Math.Abs(_timer.Interval.TotalSeconds - seconds) < 0.1;
        }
        RefreshButton.ToolTip = english ? "Refresh now" : "立即刷新";
    }

    private SolidColorBrush ThemeBrush(string key) => (SolidColorBrush)FindResource(key);

    private async void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        LocalizationService.Apply(this);
        UpdateContextMenu();
        if (_dockEdge != DockEdge.None) ConfigurePeekHandle();
        if (_lastSnapshot is { } snapshot) RenderSnapshot(snapshot);
        if (!_isDockCollapsed) await RefreshAsync();
    }

    private static string LocalizedStatus(string status) => LocalizationService.FormatTaskStatus(status);
}
