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
    private readonly AiManagerService _service;
    private readonly ResetSignalAttention _resetAttention;
    private double _resetBannerHeight;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _dockHideTimer = new() { Interval = TimeSpan.FromMilliseconds(820) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshing;
    private AiFloatingSnapshot? _lastSnapshot;
    private AiTaskSnapshot? _taskSnapshot;
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
    private const double BaseWindowHeight = 120;
    private const double SecondaryTaskRowHeight = 18;

    private enum DockEdge { None, Left, Right, Top }

    public AiFloatingWindow() : this(new AiManagerService(runtime: AiManagerRuntime.Shared)) { }

    internal AiFloatingWindow(AiManagerService service, ResetSignalAttention? resetAttention = null)
    {
        _service = service;
        _resetAttention = resetAttention ?? ResetSignalAttention.Shared;
        InitializeComponent();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) SaveWindowEnabled(true);
        };
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
        _resetAttention.Changed += ResetAttention_Changed;
        RenderResetAttention();
        AiTaskSnapshotFeed.Changed += TaskSnapshot_Changed;
        AiTaskSnapshotFeed.ProviderChanged += TaskFeed_ProviderChanged;
        if (AiTaskSnapshotFeed.Latest is { } latestTasks) TaskSnapshot_Changed(latestTasks);
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
        RenderResetAttention();

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
        AiTaskSnapshotFeed.Changed -= TaskSnapshot_Changed;
        AiTaskSnapshotFeed.ProviderChanged -= TaskFeed_ProviderChanged;
        _timer.Stop();
        _dockHideTimer.Stop();
        SavePlacement();
        // Application/update shutdown must preserve the user's choice to keep it open.
        if (!App.Current.IsExiting) SaveWindowEnabled(false);
        LocalizationService.LanguageChanged -= LocalizationService_LanguageChanged;
        _resetAttention.Changed -= ResetAttention_Changed;
        _lifetime.Cancel();
        await _service.DisposeAsync();
        _lifetime.Dispose();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private async Task RefreshAsync(bool forceRefresh = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var snapshot = await _service.RefreshFloatingAsync(_lifetime.Token, forceRefresh);
            if (!string.Equals(AiProviderModes.Normalize(snapshot.Provider),
                    AiProviderModes.Normalize(_service.Settings.Provider), StringComparison.OrdinalIgnoreCase))
                return;
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
        if (AiTaskSnapshotFeed.Latest is { } shared)
            snapshot = AiTaskSnapshotFeed.ApplyToFloating(snapshot, shared.Tasks);
        _lastSnapshot = snapshot;
        var tasks = snapshot.Tasks.Count > 0
            ? snapshot.Tasks
            : new List<AiThreadSummary>
            {
                new() { Title = snapshot.TaskName, Status = snapshot.TaskStatus, Source = snapshot.Provider,
                    ModelName = snapshot.ModelName, ReasoningEffort = snapshot.ReasoningEffort }
            };
        _displayTasks = tasks;
        UpdatePrimaryTaskRow(tasks[0]);
        UpdateBrand(snapshot);
        RenderQuotaBand(snapshot);
        SyncAgeText.Text = SyncAgeTextFor(snapshot);
        if (!_hasSizedForFirstSnapshot)
        {
            _hasSizedForFirstSnapshot = true;
            Height = BaseWindowHeight + _resetBannerHeight + Math.Clamp(tasks.Count - 1, 0, 2) * SecondaryTaskRowHeight;
        }
        RefreshVisibleTaskRows(force: true);
        ApplyStatusVisual(tasks[0].Status);
        RenderResetAttention();
    }

    private void UpdatePrimaryTaskRow(AiThreadSummary task)
    {
        TaskStatusText.Text = LocalizedStatus(task.Status);
        TaskNameText.Text = task.DisplayTitle;
        TaskNameText.ToolTip = $"{task.DisplayTitle}\n{task.SourceLabel} · {LocalizedStatus(task.Status)} · {task.ModelLabel}";
        TaskStatusText.ToolTip = TaskNameText.ToolTip;
        PrimarySourceText.Text = task.SourceLabel;
        PrimarySourceBadge.ToolTip = task.SourceLabel;
        PrimarySourceBadge.Background = ThemeBrush(task.IsDeepSeekSource ? "SignalSoft" : "InterruptedSoft");
        PrimarySourceText.Foreground = ThemeBrush(task.IsDeepSeekSource ? "Signal" : "Interrupted");
        PrimaryModelText.Text = task.ModelLabel;
        PrimaryModelText.ToolTip = task.ModelLabel;
        PrimaryTaskRow.Tag = task.Id;
        var hasLink = CanOpenTask(task);
        PrimaryTaskRow.Cursor = hasLink ? Cursors.Hand : Cursors.Arrow;
        PrimaryTaskRow.Style = hasLink ? (Style)FindResource("TaskHoverRow") : null;
    }

    /// <summary>The task list is shared by both data sources, so the header names the data source
    /// whose account and quota the numbers below belong to.</summary>
    private void UpdateBrand(AiFloatingSnapshot snapshot)
    {
        var deepSeek = snapshot.IsDeepSeek;
        BrandText.Text = deepSeek ? "DEEPSEEK / LIVE" : "CODEX / LIVE";
        BrandText.ToolTip = LocalizationService.L("当前数据源：", "Active data source: ") + (deepSeek ? "DeepSeek" : "Codex");
    }

    /// <summary>Paints both data sources' quota; the context menu can limit it to the selected one.</summary>
    private void RenderQuotaBand(AiFloatingSnapshot snapshot)
    {
        var showAll = FloatingQuotaScopes.Normalize(_service.Settings.FloatingQuotaScope) == FloatingQuotaScopes.All;
        CodexQuotaRow.Visibility = showAll || !snapshot.IsDeepSeek ? Visibility.Visible : Visibility.Collapsed;
        DeepSeekQuotaRow.Visibility = showAll || snapshot.IsDeepSeek ? Visibility.Visible : Visibility.Collapsed;

        QuotaLabelText.Text = LocalizationService.L("5h 剩余", "5h left");
        WeeklyQuotaLabelText.Text = LocalizationService.L("周剩余", "Week left");
        var fiveHour = snapshot.CodexFiveHourQuota ?? snapshot.FiveHourQuota;
        var weekly = snapshot.CodexWeeklyQuota ?? snapshot.WeeklyQuota;
        var codexError = snapshot.CodexQuotaError ?? snapshot.QuotaError;
        var codexCapturedAt = snapshot.CodexQuotaCapturedAt ?? snapshot.QuotaCapturedAt;
        WeeklyQuotaPanel.Visibility = weekly is null ? Visibility.Collapsed : Visibility.Visible;
        RenderQuotaWindow(RemainingQuotaText, fiveHour, codexError, codexCapturedAt,
            "额度窗口未知", "Unknown quota window");
        RenderQuotaWindow(WeeklyQuotaText, weekly, codexError, codexCapturedAt,
            "周额度窗口未知", "Weekly quota window unknown");

        var balanceText = snapshot.DeepSeekBalanceText ?? snapshot.QuotaText ?? "--";
        DeepSeekBalanceText.Text = balanceText;
        DeepSeekBalanceText.FontSize = (balanceText.Length > 9 ? 10 : 12) * _fontScale;
        var detail = snapshot.DeepSeekBalanceDetail ?? string.Empty;
        if (snapshot.DeepSeekBalanceCapturedAt is { } captured)
            detail += LocalizationService.L($"\n余额采集于 {captured.LocalDateTime:HH:mm:ss}",
                $"\nBalance captured at {captured.LocalDateTime:HH:mm:ss}");
        if (snapshot.DeepSeekBalanceError is { } balanceError)
        {
            detail += "\n" + LocalizationService.L("余额同步暂不可用，将自动重试。", "Balance sync unavailable; retrying automatically.");
            DeepSeekBalanceText.ToolTip = detail + "\n" + balanceError;
            DeepSeekBalanceText.SetResourceReference(TextBlock.ForegroundProperty, "WarningText");
        }
        else
        {
            DeepSeekBalanceText.ToolTip = detail;
            DeepSeekBalanceText.SetResourceReference(TextBlock.ForegroundProperty, "FloatInk");
        }
    }

    private string SyncAgeTextFor(AiFloatingSnapshot snapshot)
    {
        var error = snapshot.IsDeepSeek ? snapshot.DeepSeekBalanceError : snapshot.CodexQuotaError;
        if (error is not null) return LocalizationService.L("额度待同步", "QUOTA RETRY");
        var captured = snapshot.IsDeepSeek ? snapshot.DeepSeekBalanceCapturedAt : snapshot.CodexQuotaCapturedAt;
        return (captured ?? snapshot.CapturedAt).LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private void RenderQuotaWindow(TextBlock text, AiLimitWindow? quota, string? error, DateTimeOffset? capturedAt,
        string unknownZh, string unknownEn)
    {
        text.FontSize = 14 * _fontScale;
        text.Text = quota?.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) ?? "--";
        var detail = quota is null
            ? LocalizationService.L(unknownZh, unknownEn)
            : $"{quota.DisplayName} · {quota.WindowName}";
        if (quota?.ResetsAt is { } reset)
            detail += LocalizationService.L($"\n{reset.LocalDateTime:M月d日 HH:mm} 重置",
                $"\nResets {reset.LocalDateTime.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)}");
        if (capturedAt is { } captured)
            detail += LocalizationService.L($"\n额度采集于 {captured.LocalDateTime:HH:mm:ss}",
                $"\nQuota captured at {captured.LocalDateTime:HH:mm:ss}");
        if (error is not null)
            detail += LocalizationService.L("\n额度同步暂不可用，将自动重试。", "\nQuota sync unavailable; retrying automatically.");
        text.ToolTip = detail;
        text.SetResourceReference(TextBlock.ForegroundProperty, error is null ? "FloatInk" : "WarningText");
    }

    private void TaskSnapshot_Changed(AiTaskSnapshot snapshot)
    {
        if (_lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => TaskSnapshot_Changed(snapshot));
            return;
        }
        if (_lifetime.IsCancellationRequested || snapshot.Revision <= (_taskSnapshot?.Revision ?? 0)) return;
        _taskSnapshot = snapshot;
        // The feed merges every data source, so this publication already is the unified list.
        var projection = _lastSnapshot ??
            new AiFloatingSnapshot { Provider = AiProviderModes.Normalize(_service.Settings.Provider) };
        RenderSnapshot(AiTaskSnapshotFeed.ApplyToFloating(projection, snapshot.Tasks));
        foreach (var task in snapshot.Tasks.Take(8)) TaskMonitorTrace.Record("ui", task.Id, task.Status, snapshot.Revision, "floating");
    }

    private void TaskFeed_ProviderChanged(string provider)
    {
        if (_lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => TaskFeed_ProviderChanged(provider));
            return;
        }
        // The dashboard wrote the new provider to the shared settings file. Repaint so the
        // "only current data source" quota scope follows it, then refresh with the new credentials.
        _service.ReloadTrackedSettings();
        _hasSizedForFirstSnapshot = true;
        if (_lastSnapshot is { } snapshot) RenderSnapshot(snapshot);
        _ = RefreshAsync(forceRefresh: true);
    }

    private void RefreshVisibleTaskRows(bool force = false)
    {
        if (SecondaryTasksPanel is null) return;
        var availableHeight = ActualHeight > 0 ? ActualHeight : Height;
        var visibleTaskCount = CalculateVisibleTaskCount(availableHeight - _resetBannerHeight, _displayTasks.Count);
        if (!force && visibleTaskCount == _visibleTaskCount) return;
        _visibleTaskCount = visibleTaskCount;

        SecondaryTasksPanel.Children.Clear();
        foreach (var task in _displayTasks.Skip(1).Take(Math.Max(0, visibleTaskCount - 1)))
        {
            var waitingForUser = task.Status is "waitingOnApproval" or "waitingOnUserInput";
            var color = ThemeBrush(waitingForUser ? "WarningText" : StatusBrushKey(task.Status));
            var hasLink = CanOpenTask(task);
            var row = new Grid { Height = 18, Tag = task.Id,
                Style = hasLink ? (Style)FindResource(waitingForUser ? "AttentionTaskHoverRow" : "TaskHoverRow") : null,
                Cursor = hasLink ? Cursors.Hand : Cursors.Arrow };
            if (waitingForUser && !hasLink) row.SetResourceReference(Panel.BackgroundProperty, "WarningSoft");
            row.MouseLeftButtonDown += TaskRow_MouseLeftButtonDown;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Ellipse { Width = 4, Height = 4, Fill = color, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock
            {
                Text = task.DisplayTitle, Foreground = ThemeBrush(waitingForUser ? "WarningText" : "FloatingSecondaryInk"), FontSize = 8.5 * _fontScale,
                FontWeight = waitingForUser ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 3, 0)
            };
            Grid.SetColumn(title, 1);
            // Every row identifies its data source and the model that task used.
            var source = new TextBlock
            {
                Text = task.SourceShortLabel, Foreground = ThemeBrush(task.IsDeepSeekSource ? "Signal" : "Interrupted"),
                FontSize = 6.5 * _fontScale, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0),
                ToolTip = task.SourceLabel
            };
            Grid.SetColumn(source, 2);
            var model = new TextBlock
            {
                // Row space is scarce: the model name is shown, the reasoning effort stays in the tooltip.
                Text = task.ModelName ?? "--", Foreground = ThemeBrush("FloatMuted"),
                FontSize = 6.5 * _fontScale, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 54, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0),
                ToolTip = task.ModelLabel
            };
            Grid.SetColumn(model, 3);
            var status = new TextBlock
            {
                // Rows are narrow; the compact status keeps the title readable while the tooltip and
                // the primary badge still carry the full wording.
                Text = CompactStatus(task.Status), Foreground = color, FontSize = (waitingForUser ? 7.5 : 6.5) * _fontScale,
                FontWeight = waitingForUser ? FontWeights.Bold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(status, 4);
            row.Children.Add(dot);
            row.Children.Add(title);
            row.Children.Add(source);
            row.Children.Add(model);
            row.Children.Add(status);
            row.ToolTip = $"{task.DisplayTitle}\n{task.SourceLabel} · {LocalizedStatus(task.Status)}\n{task.ModelLabel}";
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

    private bool CanOpenTask(AiThreadSummary task) => task.IsDeepSeekSource
        ? !string.IsNullOrWhiteSpace(task.ProjectPath) && Directory.Exists(task.ProjectPath)
        : BuildTaskUri(task.Id) is not null;

    private void TaskRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        var task = _displayTasks.FirstOrDefault(item =>
            string.Equals(item.Id, row.Tag as string, StringComparison.OrdinalIgnoreCase));
        if (task is null) return;
        try
        {
            if (task.IsDeepSeekSource)
            {
                if (string.IsNullOrWhiteSpace(task.ProjectPath) || !Directory.Exists(task.ProjectPath)) return;
                e.Handled = true;
                _dockHideTimer.Stop();
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{task.ProjectPath}\"") { UseShellExecute = true });
                return;
            }
            if (BuildTaskUri(task.Id) is not { } uri) return;
            e.Handled = true;
            _dockHideTimer.Stop();
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                LocalizationService.L($"无法打开任务：{ex.Message}", $"Could not open the task: {ex.Message}"),
                LocalizationService.L("打开任务", "Open task"),
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
        var provider = _lastSnapshot?.IsDeepSeek == true ? "DeepSeek" : "Codex";
        TaskStatusText.Text = LocalizationService.IsEnglish ? "Connection issue" : "连接异常";
        TaskNameText.Text = ex is TimeoutException
            ? LocalizationService.L($"{provider} 响应较慢，将自动重试", $"{provider} is responding slowly; retrying automatically")
            : LocalizationService.L($"{provider} 状态暂时不可用", $"{provider} status is temporarily unavailable");
        TaskNameText.ToolTip = ex.Message;
        SyncAgeText.Text = "RETRY";
        ApplyStatusVisual("systemError");
    }

    private void ApplyStatusVisual(string status)
    {
        var waitingForUser = status is "waitingOnApproval" or "waitingOnUserInput";
        var colorKey = waitingForUser ? "WarningText" : StatusBrushKey(status);
        var badgeKey = status switch
        {
            "active" or "inProgress" => "SignalSoft",
            "waitingOnApproval" or "waitingOnUserInput" => "WarningSoft",
            "failed" or "systemError" => "DangerSoft",
            "interrupted" => "InterruptedSoft",
            _ => "IdleSoft"
        };
        StatusDot.Fill = ThemeBrush(colorKey);
        PeekSignal.Fill = ThemeBrush(_resetAttention.Pending is not null ? "Warning" : colorKey);
        TaskStatusText.Foreground = ThemeBrush(colorKey);
        TaskStatusText.FontWeight = waitingForUser ? FontWeights.Bold : FontWeights.SemiBold;
        TaskStatusBadge.Background = ThemeBrush(badgeKey);
        TaskStatusBadge.BorderBrush = waitingForUser ? ThemeBrush("WarningBorder") : Brushes.Transparent;
        TaskNameText.Foreground = ThemeBrush(waitingForUser ? "WarningText" : "FloatInk");
        TaskNameText.FontWeight = waitingForUser ? FontWeights.Bold : FontWeights.SemiBold;
        Shell.Background = ThemeBrush(waitingForUser ? "WarningSoft" : "FloatingShell");
        Shell.BorderBrush = ThemeBrush(waitingForUser ? "WarningBorder" : "Line");
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

    private void SaveWindowEnabled(bool enabled)
    {
        try
        {
            var settings = _service.ReloadSettings();
            settings.FloatingWindowEnabled = enabled;
            _service.SaveSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[AiFloatingWindow] Could not save open state: {ex.Message}");
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
        PeekHandle.ToolTip = _resetAttention.Pending is not null
            ? _resetAttention.Title + "\n" + _resetAttention.Detail
            : LocalizationService.IsEnglish ? "Hover to expand" : "悬停展开";
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

    private async void RefreshNow_Click(object sender, RoutedEventArgs e) => await RefreshAsync(forceRefresh: true);

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

    private void QuotaScope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var settings = _service.ReloadSettings();
        settings.FloatingQuotaScope = FloatingQuotaScopes.Normalize(item.Tag?.ToString());
        _service.SaveSettings(settings);
        UpdateContextMenu();
        _hasSizedForFirstSnapshot = true;
        if (_lastSnapshot is { } snapshot) RenderSnapshot(snapshot);
        _ = RefreshAsync();
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
        QuotaScopeMenuItem.Header = english ? "Quota display" : "额度显示";
        AllQuotaMenuItem.Header = english ? "All data sources" : "全部额度";
        ActiveQuotaMenuItem.Header = english ? "Selected source only" : "仅当前数据源";
        var quotaScope = FloatingQuotaScopes.Normalize(_service.Settings.FloatingQuotaScope);
        AllQuotaMenuItem.IsChecked = quotaScope == FloatingQuotaScopes.All;
        ActiveQuotaMenuItem.IsChecked = quotaScope == FloatingQuotaScopes.Active;
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

    private void ResetAttention_Changed(object? sender, EventArgs e) => RenderResetAttention();

    private void RenderResetAttention()
    {
        var visible = _lastSnapshot?.IsDeepSeek != true && _resetAttention.Pending is not null;
        ResetAttentionPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ResetAttentionButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ResetAttentionTitle.Text = "● " + _resetAttention.Title;
        var hasTime = _resetAttention.Pending?.LocalResetTimeCompactText is { Length: > 0 };
        ResetAttentionTime.Text = hasTime ? _resetAttention.TimeText : "";
        ResetAttentionTime.Visibility = hasTime ? Visibility.Visible : Visibility.Collapsed;
        ResetAttentionAction.Text = _resetAttention.DismissText;
        ResetAttentionButton.ToolTip = _resetAttention.Detail;
        var sourceUrl = CodexResetClient.SafeSourceUrl(_resetAttention.Pending?.Url);
        ResetAttentionSourceButton.Visibility = sourceUrl is null ? Visibility.Collapsed : Visibility.Visible;
        ResetAttentionSourceButton.Tag = sourceUrl;
        ResetAttentionSourceButton.ToolTip = sourceUrl;
        ResetAttentionSourceButton.Content = LocalizationService.L("查看原文", "View source");
        ResetAttentionSourceButton.Height = 20 * _fontScale;
        System.Windows.Automation.AutomationProperties.SetName(ResetAttentionButton,
            $"{_resetAttention.Title}. {_resetAttention.Detail}. {_resetAttention.DismissText}");
        ResetAttentionButton.Height = visible ? (hasTime ? 64 : 44) * _fontScale : 0;
        var height = visible ? ResetAttentionButton.Height + (sourceUrl is null ? 0 : 24 * _fontScale) + 2 : 0;
        ResetAttentionPanel.Height = height;
        var delta = height - _resetBannerHeight;
        _resetBannerHeight = height;
        MinHeight = BaseWindowHeight + height;
        if (Math.Abs(delta) > 0.1) Height = Math.Max(MinHeight, Height + delta);
        if (IsLoaded && !_isDockCollapsed && Math.Abs(delta) > 0.1)
        {
            var area = GetCurrentMonitorWorkArea();
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        }
        RefreshVisibleTaskRows(force: true);
        if (_dockEdge != DockEdge.None) ConfigurePeekHandle();
        if (visible)
        {
            PeekSignal.Fill = ThemeBrush("Warning");
            PeekHandle.ToolTip = _resetAttention.Title + "\n" + _resetAttention.Detail;
        }
        else if (_lastSnapshot is { } snapshot) ApplyStatusVisual(snapshot.TaskStatus);
    }

    private void ResetAttentionSource_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var url = CodexResetClient.SafeSourceUrl(_resetAttention.Pending?.Url);
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, LocalizationService.L("无法打开链接：", "Unable to open link: ") + ex.Message,
                "AIMaster", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DismissResetAttention_Click(object sender, RoutedEventArgs e)
    {
        try { _resetAttention.Dismiss(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, LocalizationService.L("重置提示隐藏失败：", "Unable to dismiss reset reminder: ") + ex.Message,
                "AIMaster", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void LocalizationService_LanguageChanged(object? sender, EventArgs e)
    {
        LocalizationService.Apply(this);
        UpdateContextMenu();
        if (_dockEdge != DockEdge.None) ConfigurePeekHandle();
        RenderResetAttention();
        if (_lastSnapshot is { } snapshot) RenderSnapshot(snapshot);
        if (IsLoaded && !_isDockCollapsed) await RefreshAsync();
    }

    private static string LocalizedStatus(string status) => LocalizationService.FormatTaskStatus(status);

    /// <summary>Compact row status; the full wording stays in the primary badge and tooltips.</summary>
    private static string CompactStatus(string status) => status switch
    {
        "waitingOnApproval" => LocalizationService.L("待批准", "Approve"),
        "waitingOnUserInput" => LocalizationService.L("待输入", "Input"),
        "active" or "inProgress" => LocalizationService.L("运行中", "Running"),
        "completed" => LocalizationService.L("已完成", "Done"),
        "failed" or "systemError" => LocalizationService.L("失败", "Failed"),
        "interrupted" => LocalizationService.L("已中断", "Stopped"),
        "notLoaded" => "--",
        _ => LocalizationService.FormatTaskStatus(status)
    };
}
