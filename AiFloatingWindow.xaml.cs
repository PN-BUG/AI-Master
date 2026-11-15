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
    private double _quotaBandHeight = 40;
    private readonly DispatcherTimer _timer = new();
    // 顶栏右侧的“多久前更新”需要每秒重算，但它不触发任何网络/IO。
    private readonly DispatcherTimer _ageTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _dockHideTimer = new() { Interval = TimeSpan.FromMilliseconds(820) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshing;
    private Exception? _headerError;
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
        RenderQuotaBand(new AiFloatingSnapshot { Provider = _service.Settings.Provider });
        ApplyRefreshInterval(_service.Settings.FloatingRefreshSeconds);
        _timer.Tick += async (_, _) =>
        {
            if (!_isDockCollapsed) await RefreshAsync();
        };
        _ageTimer.Tick += (_, _) => UpdateAgeText();
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

        if (_lastSnapshot is { } snapshot)
        {
            RenderQuotaBand(snapshot, resizeWindow: true);
            RenderSnapshot(snapshot);
        }
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
        _ageTimer.Start();
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
        _ageTimer.Stop();
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
        RenderQuotaBand(snapshot);
        _headerError = null;
        UpdateHeader(snapshot, tasks);
        if (!_hasSizedForFirstSnapshot)
        {
            _hasSizedForFirstSnapshot = true;
            Height = MinHeight + Math.Clamp(tasks.Count - 1, 0, 2) * SecondaryTaskRowHeight;
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

    /// <summary>
    /// Paints the configurable top line (see <see cref="FloatingHeaderStyles"/>). Every style shares
    /// the same row — live dot, style text, data-freshness label — so switching never moves the
    /// window or hides the chrome buttons.
    /// </summary>
    private void UpdateHeader(AiFloatingSnapshot snapshot, IReadOnlyList<AiThreadSummary> tasks)
    {
        var style = FloatingHeaderStyles.Normalize(_service.Settings.FloatingHeaderStyle);
        HeaderTrendPanel.Visibility = Visibility.Collapsed;
        HeaderTrendPanel.Children.Clear();
        HeaderSummaryText.Visibility = Visibility.Visible;
        HeaderSummaryText.Text = string.Empty;
        var colorKey = "FloatInk";

        switch (style)
        {
            case FloatingHeaderStyles.Brand:
                HeaderSummaryText.Text = snapshot.IsDeepSeek ? "DEEPSEEK / LIVE" : "CODEX / LIVE";
                colorKey = "FloatSignal";
                break;
            case FloatingHeaderStyles.Minimal:
                HeaderSummaryText.Visibility = Visibility.Collapsed;
                break;
            case FloatingHeaderStyles.TodayUsage:
                var usage = FormatTodayUsage(snapshot);
                HeaderSummaryText.Text = usage.Text;
                colorKey = usage.ColorKey;
                break;
            case FloatingHeaderStyles.QuotaAlert:
                // Everything is fine: fall back to the task overview instead of showing an empty alert.
                HeaderSummaryText.Text = FormatTaskOverview(tasks);
                colorKey = "FloatMuted";
                if (FormatQuotaAlert(snapshot, _service.Settings.WarningPercent,
                        _service.Settings.DeepSeekBalanceWarning) is { } alert)
                {
                    HeaderSummaryText.Text = alert.Text;
                    colorKey = alert.ColorKey;
                }
                break;
            case FloatingHeaderStyles.LatestEvent:
                HeaderSummaryText.Text = FormatLatestEvent(tasks,
                    _resetAttention.Pending?.AnnouncedAt,
                    _resetAttention.Pending is not null ? _resetAttention.Title : null,
                    DateTimeOffset.Now);
                break;
            case FloatingHeaderStyles.UsageTrend:
                colorKey = RenderHeaderTrend(snapshot);
                break;
            default:
                HeaderSummaryText.Text = FormatQuotaCountdown(snapshot, DateTimeOffset.Now);
                if (HeaderSummaryText.Text.Length == 0) HeaderSummaryText.Text = FormatTaskOverview(tasks);
                if (FormatQuotaAlert(snapshot, _service.Settings.WarningPercent,
                        _service.Settings.DeepSeekBalanceWarning) is { } tight) colorKey = tight.ColorKey;
                break;
        }

        HeaderSummaryText.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        var tooltip = BuildHeaderTooltip(snapshot, tasks, style);
        HeaderSummaryText.ToolTip = tooltip;
        HeaderInfoPanel.ToolTip = tooltip;
        UpdateAgeText();
    }

    private void RenderHeaderError(Exception error)
    {
        HeaderTrendPanel.Visibility = Visibility.Collapsed;
        HeaderTrendPanel.Children.Clear();
        var minimal = FloatingHeaderStyles.Normalize(_service.Settings.FloatingHeaderStyle) ==
                      FloatingHeaderStyles.Minimal;
        HeaderSummaryText.Visibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        HeaderSummaryText.Text = LocalizationService.L("同步异常", "Sync issue");
        HeaderSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
        HeaderSummaryText.ToolTip = error.Message;
        HeaderInfoPanel.ToolTip = error.Message;
        SyncAgeText.Text = LocalizationService.L("重试中", "RETRY");
    }

    /// <summary>Recomputes the freshness label; called every second and on every render.</summary>
    private void UpdateAgeText()
    {
        if (_headerError is not null)
        {
            SyncAgeText.Text = LocalizationService.L("重试中", "RETRY");
            return;
        }
        if (_lastSnapshot is not { } snapshot)
        {
            SyncAgeText.Text = string.Empty;
            return;
        }
        var sourceError = snapshot.IsDeepSeek
            ? snapshot.GoQuota is not null ? snapshot.GoQuotaError : snapshot.DeepSeekBalanceError
            : snapshot.CodexQuotaError;
        if (sourceError is not null)
        {
            SyncAgeText.Text = LocalizationService.L("待同步", "STALE");
            return;
        }
        var style = FloatingHeaderStyles.Normalize(_service.Settings.FloatingHeaderStyle);
        var captured = style is FloatingHeaderStyles.TodayUsage or FloatingHeaderStyles.UsageTrend
            ? snapshot.LocalUsageCapturedAt ?? CapturedAtFor(snapshot)
            : CapturedAtFor(snapshot);
        SyncAgeText.Text = style == FloatingHeaderStyles.Brand
            ? captured.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : FormatFreshness(DateTimeOffset.Now - captured);
    }

    private static DateTimeOffset CapturedAtFor(AiFloatingSnapshot snapshot)
    {
        var captured = snapshot.IsDeepSeek
            ? snapshot.GoQuotaCapturedAt ?? snapshot.DeepSeekBalanceCapturedAt
            : snapshot.CodexQuotaCapturedAt;
        return captured ?? snapshot.CapturedAt;
    }

    /// <summary>Draws the seven-day token sparkline and returns the summary text colour key.</summary>
    private string RenderHeaderTrend(AiFloatingSnapshot snapshot)
    {
        var daily = HeaderDailyTokens(snapshot);
        if (daily.Count == 0)
        {
            HeaderSummaryText.Text = LocalizationService.L("用量加载中", "Loading usage");
            return "FloatMuted";
        }
        var maximum = daily.Max(item => item.Tokens);
        var today = DateOnly.FromDateTime(DateTime.Now);
        HeaderTrendPanel.Visibility = Visibility.Visible;
        HeaderTrendPanel.Children.Clear();
        foreach (var day in daily)
        {
            var ratio = maximum <= 0 ? 0 : day.Tokens / (double)maximum;
            var bar = new Rectangle
            {
                Width = 2,
                Height = 2 + 9 * ratio,
                Margin = new Thickness(0, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                RadiusX = 1,
                RadiusY = 1,
                Fill = ThemeBrush(day.Date == today ? "Signal" : "FloatMuted"),
                Opacity = day.Date == today ? 1 : 0.55
            };
            HeaderTrendPanel.Children.Add(bar);
        }
        var todayTokens = daily.FirstOrDefault(item => item.Date == today)?.Tokens ?? 0;
        HeaderSummaryText.Text = LocalizationService.L($"今日 {FormatTokenCount(todayTokens)}",
            $"Today {FormatTokenCount(todayTokens)}");
        return "FloatInk";
    }

    private string BuildHeaderTooltip(AiFloatingSnapshot snapshot, IReadOnlyList<AiThreadSummary> tasks,
        string style)
    {
        var english = LocalizationService.IsEnglish;
        var captured = CapturedAtFor(snapshot);
        var lines = new List<string>
        {
            (english ? "Header: " : "顶栏：") + HeaderStyleCaption(style),
            (english ? "Active source: " : "当前数据源：") + (snapshot.IsDeepSeek ? "DeepSeek" : "Codex"),
            (english ? "Updated " : "更新时间：") + captured.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
            " · " + FormatFreshness(DateTimeOffset.Now - captured),
            (english ? "Auto refresh: " : "自动刷新：") + (english
                ? $"{_timer.Interval.TotalSeconds:0} s"
                : $"{_timer.Interval.TotalSeconds:0} 秒")
        };
        var fiveHour = snapshot.CodexFiveHourQuota ?? snapshot.FiveHourQuota;
        var weekly = snapshot.CodexWeeklyQuota ?? snapshot.WeeklyQuota;
        if (fiveHour is not null)
            lines.Add((english ? "5h left " : "5h 剩余 ") +
                      $"{fiveHour.RemainingPercent:0.#}%" + ResetSuffix(fiveHour));
        if (weekly is not null)
            lines.Add((english ? "Week left " : "周剩余 ") +
                      $"{weekly.RemainingPercent:0.#}%" + ResetSuffix(weekly));
        if (snapshot.GoQuota is { } goQuota)
        {
            foreach (var window in goQuota.Windows)
                lines.Add(window.ShortLabel + " " + window.RemainingText + " " +
                          LocalizationService.L("剩余", "left") + ResetSuffix(window));
        }
        else if (snapshot.DeepSeekBalance is { } balance)
            lines.Add((english ? "Balance " : "余额 ") + balance.TotalText);
        if (snapshot.LocalCodexUsage is { } local)
            lines.Add(english
                ? $"Local Codex tokens — today {FormatTokenCount(local.TodayTokens)}, week {FormatTokenCount(local.TotalTokens)}"
                : $"本机 Codex 会话 — 今日 {FormatTokenCount(local.TodayTokens)}，本周 {FormatTokenCount(local.TotalTokens)}");
        if (snapshot.DeepSeekUsage is { } deepSeek)
            lines.Add(english
                ? $"DeepSeek today {deepSeek.Money(deepSeek.TodayCost)} · {FormatTokenCount(deepSeek.TodayTokens)} tokens"
                : $"DeepSeek 今日 {deepSeek.Money(deepSeek.TodayCost)} · {FormatTokenCount(deepSeek.TodayTokens)} tokens");
        lines.Add((english ? "Tasks: " : "任务：") + FormatTaskOverview(tasks));
        lines.Add(english
            ? "Right-click → Header text to switch."
            : "右键 → 顶栏显示 可切换样式。");
        return string.Join("\n", lines);
    }

    private static string ResetSuffix(AiLimitWindow quota) =>
        quota.ResetsAt is { } resetsAt ? " · " + FormatCountdown(resetsAt - DateTimeOffset.Now) : string.Empty;

    private static string ResetSuffix(AiGoQuotaWindow quota) =>
        quota.ResetsAt is { } resetsAt ? " · " + FormatCountdown(resetsAt - DateTimeOffset.Now) : string.Empty;

    internal static string HeaderStyleCaption(string style) => FloatingHeaderStyles.Normalize(style) switch
    {
        FloatingHeaderStyles.Brand => LocalizationService.L("数据源 + 时间", "Source + clock"),
        FloatingHeaderStyles.TodayUsage => LocalizationService.L("今日消耗", "Today's usage"),
        FloatingHeaderStyles.QuotaAlert => LocalizationService.L("额度预警", "Quota alerts"),
        FloatingHeaderStyles.LatestEvent => LocalizationService.L("最近事件", "Latest event"),
        FloatingHeaderStyles.UsageTrend => LocalizationService.L("7 日用量", "Usage trend"),
        FloatingHeaderStyles.Minimal => LocalizationService.L("极简", "Minimal"),
        _ => LocalizationService.L("额度倒计时", "Quota countdown")
    };

    /// <summary>Remaining quota of the tightest window plus its reset countdown (empty when unknown).</summary>
    internal static string FormatQuotaCountdown(AiFloatingSnapshot snapshot, DateTimeOffset now)
    {
        // A subscription plan reports percentages of its own windows, so the countdown follows
        // the plan allowance instead of the account balance while a Go snapshot is present.
        if (snapshot.IsDeepSeek && snapshot.GoQuota?.Tightest is { } goWindow)
        {
            var goText = $"{goWindow.ShortLabel} {goWindow.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture)}%";
            if (goWindow.ResetsAt is { } goReset) goText += " · " + FormatResetCountdown(goReset - now);
            return goText;
        }
        var fiveHour = snapshot.CodexFiveHourQuota ?? snapshot.FiveHourQuota;
        var weekly = snapshot.CodexWeeklyQuota ?? snapshot.WeeklyQuota;
        var quota = new[] { fiveHour, weekly }.Where(item => item is not null)
            .OrderBy(item => item!.RemainingPercent).FirstOrDefault();
        if (quota is null) return string.Empty;
        var weeklySelected = weekly is not null && ReferenceEquals(quota, weekly);
        // 顶栏只有一行，标签和倒计时都用最短写法；完整说明留在悬停提示里。
        var label = weeklySelected
            ? LocalizationService.L("周", "Week")
            : LocalizationService.L("5h", "5h");
        var remaining = quota.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture);
        var text = $"{label} {remaining}%";
        if (quota.ResetsAt is { } resetsAt) text += " · " + FormatResetCountdown(resetsAt - now);
        return text;
    }

    /// <summary>One-unit countdown for the single-line header; the tooltip keeps exact minutes.</summary>
    internal static string FormatResetCountdown(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return LocalizationService.L("即将重置", "resetting now");
        if (span.TotalMinutes < 1) return LocalizationService.L("马上重置", "resets in <1 min");
        if (span.TotalHours < 1)
            return LocalizationService.L($"{span.Minutes}m后重置", $"resets in {span.Minutes}m");
        if (span.TotalDays < 1)
            return LocalizationService.L($"{(int)span.TotalHours}h后重置", $"resets in {(int)span.TotalHours}h");
        var days = (int)span.TotalDays;
        return span.Hours == 0
            ? LocalizationService.L($"{days}d后重置", $"resets in {days}d")
            : LocalizationService.L($"{days}d{span.Hours}h后重置", $"resets in {days}d{span.Hours}h");
    }

    internal static string FormatCountdown(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return LocalizationService.L("即将重置", "resetting now");
        if (span.TotalMinutes < 1) return LocalizationService.L("不到 1 分钟后重置", "resets in <1 min");
        if (span.TotalHours < 1)
            return LocalizationService.L($"{span.Minutes} 分钟后重置", $"resets in {span.Minutes} min");
        if (span.TotalDays < 1)
        {
            var hours = (int)span.TotalHours;
            return span.Minutes == 0
                ? LocalizationService.L($"{hours} 小时后重置", $"resets in {hours}h")
                : LocalizationService.L($"{hours} 小时 {span.Minutes} 分后重置",
                    $"resets in {hours}h {span.Minutes}m");
        }
        var days = (int)span.TotalDays;
        return span.Hours == 0
            ? LocalizationService.L($"{days} 天后重置", $"resets in {days}d")
            : LocalizationService.L($"{days} 天 {span.Hours} 小时后重置", $"resets in {days}d {span.Hours}h");
    }

    /// <summary>Compact task counts used by the alert style and by the header tooltip.</summary>
    internal static string FormatTaskOverview(IReadOnlyList<AiThreadSummary> tasks)
    {
        var waiting = tasks.Count(task => task.Status is "waitingOnApproval" or "waitingOnUserInput");
        var running = tasks.Count(task => task.Status is "active" or "inProgress");
        var errors = tasks.Count(task => task.Status is "failed" or "systemError");
        var parts = new List<string>();
        if (errors > 0) parts.Add(LocalizationService.L($"{errors} 异常", $"{errors} failed"));
        if (waiting > 0) parts.Add(LocalizationService.L($"{waiting} 待确认", $"{waiting} waiting"));
        if (running > 0) parts.Add(LocalizationService.L($"{running} 运行中", $"{running} running"));
        if (parts.Count > 0) return string.Join(" · ", parts.Take(2));
        return tasks.Count == 0
            ? LocalizationService.L("暂无任务", "No tasks")
            : LocalizationService.L($"空闲 · {tasks.Count} 个任务", $"Idle · {tasks.Count} tasks");
    }

    /// <summary>Quota or balance below the configured warning thresholds, or null when all is well.</summary>
    internal static (string Text, string ColorKey)? FormatQuotaAlert(AiFloatingSnapshot snapshot,
        double warningPercent, double balanceWarning)
    {
        var alerts = new List<string>();
        var danger = false;
        if (snapshot.IsDeepSeek && snapshot.GoQuota is { } goQuota)
        {
            foreach (var window in goQuota.Windows.Where(window => window.UsedPercent >= warningPercent))
            {
                alerts.Add(LocalizationService.L($"{window.ShortLabel} 仅剩 {window.RemainingText}",
                    $"{window.ShortLabel} {window.RemainingText} left"));
                if (window.IsExhausted) danger = true;
            }
            if (snapshot.GoQuotaError is { } goError && !goQuota.HasAnyData)
                alerts.Add(LocalizationService.L("套餐额度同步异常", "Plan allowance sync issue"));
        }
        else
        {
            var weekly = snapshot.CodexWeeklyQuota ?? snapshot.WeeklyQuota;
            var fiveHour = snapshot.CodexFiveHourQuota ?? snapshot.FiveHourQuota;
            if (weekly is not null && weekly.UsedPercent >= warningPercent)
                alerts.Add(LocalizationService.L($"周额度仅剩 {weekly.RemainingPercent:0.#}%",
                    $"Week {weekly.RemainingPercent:0.#}% left"));
            if (fiveHour is not null && fiveHour.UsedPercent >= warningPercent)
                alerts.Add(LocalizationService.L($"5h 额度仅剩 {fiveHour.RemainingPercent:0.#}%",
                    $"5h {fiveHour.RemainingPercent:0.#}% left"));
            if (balanceWarning > 0 && snapshot.DeepSeekBalance is { } balance && balance.Total < balanceWarning)
            {
                alerts.Add(LocalizationService.L($"余额 {balance.TotalText}", $"Balance {balance.TotalText}"));
                danger = true;
            }
        }
        if (alerts.Count == 0) return null;
        return (string.Join(" · ", alerts.Take(2)), danger ? "DangerText" : "WarningText");
    }

    /// <summary>Today's spend/tokens for the active data source, with a safe fallback to local usage.</summary>
    internal static (string Text, string ColorKey) FormatTodayUsage(AiFloatingSnapshot snapshot)
    {
        var local = snapshot.LocalCodexUsage;
        var deepSeek = snapshot.DeepSeekUsage;
        if (snapshot.IsDeepSeek && deepSeek is not null)
        {
            var spend = deepSeek.Money(deepSeek.TodayCost);
            var tokens = FormatTokenCount(deepSeek.TodayTokens);
            return (LocalizationService.L($"今日 {spend} · {tokens}",
                $"Today {spend} · {tokens}"), "FloatInk");
        }
        if (local is not null)
        {
            var today = FormatTokenCount(local.TodayTokens);
            var week = FormatTokenCount(local.TotalTokens);
            return (LocalizationService.L($"今日 {today} · 本周 {week}",
                $"Today {today} · Week {week}"), "FloatInk");
        }
        if (deepSeek is not null)
        {
            var tokens = FormatTokenCount(deepSeek.TodayTokens);
            return (LocalizationService.L($"今日 {tokens}", $"Today {tokens}"), "FloatInk");
        }
        if (snapshot.DeepSeekBalance is { } balance)
            return (LocalizationService.L($"余额 {balance.TotalText}", $"Balance {balance.TotalText}"), "FloatInk");
        return (LocalizationService.L("用量加载中", "Loading usage"), "FloatMuted");
    }

    /// <summary>The newest task activity or reset announcement, whichever happened last.</summary>
    internal static string FormatLatestEvent(IReadOnlyList<AiThreadSummary> tasks, DateTimeOffset? resetAt,
        string? resetTitle, DateTimeOffset now)
    {
        var latest = tasks.Where(task => task.UpdatedAt is not null)
            .OrderByDescending(task => task.UpdatedAt).FirstOrDefault();
        var taskAt = latest?.UpdatedAt;
        if (resetAt is not null && (taskAt is null || resetAt >= taskAt))
        {
            var title = resetTitle ?? LocalizationService.L("额度重置", "Quota reset");
            return $"{FormatClock(resetAt.Value, now)} {title}";
        }
        if (latest?.UpdatedAt is not { } updatedAt)
            return LocalizationService.L("暂无最近事件", "No recent event");
        var prefix = $"{FormatClock(updatedAt, now)} {LocalizationService.FormatTaskStatus(latest.Status)}";
        return string.IsNullOrWhiteSpace(latest.DisplayTitle) ? prefix : $"{prefix} · {latest.DisplayTitle}";
    }

    /// <summary>Seven days of local Codex tokens (or DeepSeek platform tokens as a fallback).</summary>
    internal static IReadOnlyList<AiDailyUsage> HeaderDailyTokens(AiFloatingSnapshot snapshot)
    {
        var start = DateOnly.FromDateTime(DateTime.Now).AddDays(-6);
        var local = snapshot.LocalCodexUsage?.DailyUsage;
        if (local is not null && local.Any(item => item.Tokens > 0))
            return local.Where(item => item.Date >= start)
                .GroupBy(item => item.Date)
                .Select(group => new AiDailyUsage { Date = group.Key, Tokens = group.Sum(item => item.Tokens) })
                .OrderBy(item => item.Date)
                .ToList();
        var deepSeek = snapshot.DeepSeekUsage?.Daily;
        if (deepSeek is not null)
            return deepSeek.Where(item => item.Date >= start)
                .Select(item => new AiDailyUsage { Date = item.Date, Tokens = item.Tokens })
                .OrderBy(item => item.Date)
                .TakeLast(7)
                .ToList();
        return Array.Empty<AiDailyUsage>();
    }

    internal static string FormatFreshness(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 5) return LocalizationService.L("刚刚", "now");
        if (age.TotalMinutes < 1)
            return LocalizationService.L($"{age.Seconds} 秒前", $"{age.Seconds}s ago");
        if (age.TotalHours < 1)
            return LocalizationService.L($"{age.Minutes} 分钟前", $"{age.Minutes}m ago");
        return LocalizationService.L($"{age.Hours} 小时前", $"{age.Hours}h ago");
    }

    internal static string FormatTokenCount(long tokens)
    {
        if (tokens >= 1_000_000_000)
            return (tokens / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        if (tokens >= 1_000_000)
            return (tokens / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (tokens >= 1_000)
            return (tokens / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatClock(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.LocalDateTime;
        if (local.Date == now.LocalDateTime.Date)
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return LocalizationService.IsEnglish
            ? local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("M月d日 HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>Paints both data sources' quota; the context menu can limit it to the selected one.</summary>
    private void RenderQuotaBand(AiFloatingSnapshot snapshot, bool resizeWindow = false)
    {
        var showAll = FloatingQuotaScopes.Normalize(_service.Settings.FloatingQuotaScope) == FloatingQuotaScopes.All;
        var inScope = showAll || snapshot.IsDeepSeek;
        // A configured OpenCode Go plan replaces the pay-as-you-go balance row: the subscription
        // allowance is what actually gates the DeepSeek work, and the floating monitor is opened to
        // see whether that work can continue.
        var goQuota = snapshot.GoQuota;
        var settings = _service.Settings;
        var codexInScope = showAll || !snapshot.IsDeepSeek;
        CodexQuotaRow.Visibility = codexInScope && settings.FloatingShowFiveHourQuota ? Visibility.Visible : Visibility.Collapsed;
        GoQuotaRow.Visibility = goQuota is not null && inScope && settings.FloatingShowDeepSeekQuota ? Visibility.Visible : Visibility.Collapsed;
        DeepSeekQuotaRow.Visibility = goQuota is null && inScope && settings.FloatingShowDeepSeekQuota ? Visibility.Visible : Visibility.Collapsed;

        QuotaLabelText.Text = LocalizationService.L("5h 剩余", "5h left");
        WeeklyQuotaLabelText.Text = LocalizationService.L("周剩余", "Week left");
        var fiveHour = snapshot.CodexFiveHourQuota ?? snapshot.FiveHourQuota;
        var weekly = snapshot.CodexWeeklyQuota ?? snapshot.WeeklyQuota;
        var codexError = snapshot.CodexQuotaError ?? snapshot.QuotaError;
        var codexCapturedAt = snapshot.CodexQuotaCapturedAt ?? snapshot.QuotaCapturedAt;
        WeeklyQuotaPanel.Visibility = weekly is not null && codexInScope && settings.FloatingShowWeeklyQuota
            ? Visibility.Visible : Visibility.Collapsed;
        RenderQuotaWindow(RemainingQuotaText, fiveHour, codexError, codexCapturedAt,
            "额度窗口未知", "Unknown quota window");
        RenderQuotaWindow(WeeklyQuotaText, weekly, codexError, codexCapturedAt,
            "周额度窗口未知", "Weekly quota window unknown");

        var balanceText = snapshot.DeepSeekBalanceText ?? snapshot.QuotaText ?? "--";
        DeepSeekBalanceText.Text = balanceText;
        DeepSeekBalanceLabel.Text = LocalizationService.L("余额", "Balance");
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

        if (goQuota is not null) RenderGoQuota(goQuota, snapshot.GoQuotaError, snapshot.GoQuotaCapturedAt);
        UpdateFooterLayout(resizeWindow);
    }

    private void UpdateFooterLayout(bool resizeWindow = false)
    {
        var metrics = QuotaSummaryGrid.Children.OfType<FrameworkElement>()
            .Where(element => element.Visibility == Visibility.Visible).ToArray();
        if (QuotaSummaryGrid.ColumnDefinitions.Count != metrics.Length)
        {
            QuotaSummaryGrid.ColumnDefinitions.Clear();
            for (var index = 0; index < metrics.Length; index++)
                QuotaSummaryGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (var index = 0; index < metrics.Length; index++)
        {
            Grid.SetColumn(metrics[index], index);
            metrics[index].Margin = new Thickness(index == 0 ? 0 : 5, 0, index == metrics.Length - 1 ? 0 : 5, 0);
        }
        QuotaSummaryGrid.Visibility = metrics.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        GoQuotaRow.Margin = new Thickness(0, metrics.Length > 0 ? 5 : 0, 0, 0);
        var visible = _service.Settings.FloatingFooterVisible &&
                      (metrics.Length > 0 || GoQuotaRow.Visibility == Visibility.Visible);
        QuotaBand.Visibility = QuotaDivider.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateFooterHeight(resizeWindow);
    }

    private void UpdateFooterHeight(bool resizeWindow = false)
    {
        if (QuotaBand is null) return;
        // Reserve the measured footer height, including larger fonts and Go's extra row.
        QuotaBand.Measure(new Size(Math.Max(1, Width - 25), double.PositiveInfinity));
        var height = QuotaBand.Visibility == Visibility.Visible ? Math.Ceiling(QuotaBand.DesiredSize.Height) + 1 : 0;
        if (Math.Abs(height - _quotaBandHeight) < 0.1) return;
        _quotaBandHeight = height;
        UpdateMinimumHeight(resizeWindow);
    }

    private void UpdateMinimumHeight(bool resizeWindow = false)
    {
        var previous = MinHeight;
        // The header and shell padding have fixed heights; only the primary task's
        // text scales. Measuring it avoids reserving an entire scaled block of empty space.
        PrimaryTaskRow.Measure(new Size(Math.Max(1, Width - 25), double.PositiveInfinity));
        MinHeight = 17 + 18 + Math.Ceiling(PrimaryTaskRow.DesiredSize.Height) + 12
            + _quotaBandHeight + _resetBannerHeight;
        if (Math.Abs(MinHeight - previous) < 0.1) return;
        if (_hasSizedForFirstSnapshot)
            Height = Math.Max(MinHeight, Height + (resizeWindow ? MinHeight - previous : 0));
        if (IsLoaded && !_isDockCollapsed)
        {
            var area = GetCurrentMonitorWorkArea();
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        }
    }

    /// <summary>
    /// Paints the OpenCode Go allowance: remaining share of the rolling five-hour, weekly and
    /// monthly windows. A window the endpoint did not report keeps a placeholder so the three
    /// columns stay aligned while the plan data is retried.
    /// </summary>
    private void RenderGoQuota(AiGoQuotaSnapshot quota, string? error, DateTimeOffset? capturedAt)
    {
        GoRollingLabel.Text = LocalizationService.L("套餐 5h", "Go 5h");
        GoWeeklyLabel.Text = LocalizationService.L("套餐 周", "Go week");
        GoMonthlyLabel.Text = LocalizationService.L("套餐 月", "Go month");
        RenderGoQuotaWindow(GoRollingText, quota.Rolling, error, capturedAt);
        RenderGoQuotaWindow(GoWeeklyText, quota.Weekly, error, capturedAt);
        RenderGoQuotaWindow(GoMonthlyText, quota.Monthly, error, capturedAt);
    }

    private void RenderGoQuotaWindow(TextBlock text, AiGoQuotaWindow? window, string? error,
        DateTimeOffset? capturedAt)
    {
        text.FontSize = 14 * _fontScale;
        text.Text = window?.RemainingPercent.ToString("0.#", CultureInfo.InvariantCulture) ?? "--";
        if (window is null)
        {
            text.ToolTip = error ?? LocalizationService.L("套餐额度尚未同步", "Plan allowance not loaded yet");
            text.SetResourceReference(TextBlock.ForegroundProperty,
                error is null ? "FloatMuted" : "WarningText");
            return;
        }

        var detail = $"{LocalizationService.L("OpenCode Go 套餐", "OpenCode Go plan")} · " +
                     $"{window.ShortLabel} · {window.WindowName}" +
                     $"\n{window.UsedText} · {window.RemainingText} " +
                     LocalizationService.L("剩余", "left");
        if (window.ResetsAt is { } reset)
            detail += LocalizationService.L($"\n{reset.LocalDateTime:M月d日 HH:mm} 重置",
                $"\nResets {reset.LocalDateTime.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)}");
        if (capturedAt is { } captured)
            detail += LocalizationService.L($"\n额度采集于 {captured.LocalDateTime:HH:mm:ss}",
                $"\nQuota captured at {captured.LocalDateTime:HH:mm:ss}");
        if (error is not null)
            detail += "\n" + error;
        text.ToolTip = detail;
        text.SetResourceReference(TextBlock.ForegroundProperty,
            error is not null || window.IsExhausted ? "WarningText" : "FloatInk");
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
        PrimaryTaskRow.Measure(new Size(Math.Max(1, Width - 25), double.PositiveInfinity));
        var fixedHeight = 18 + 17 + Math.Ceiling(PrimaryTaskRow.DesiredSize.Height)
            + TaskRowsPanel.Margin.Top + TaskRowsPanel.Margin.Bottom
            + _quotaBandHeight + _resetBannerHeight + 2;
        // Saved window height can outlive the tasks it used to hold. Do not leave
        // a star-sized empty row when every available task already fits.
        var contentHeight = Math.Max(MinHeight,
            fixedHeight + Math.Max(0, _displayTasks.Count - 1) * SecondaryTaskRowHeight);
        if (!_isDockCollapsed && Height > contentHeight + 0.5) Height = contentHeight;
        var availableHeight = Height;
        var visibleTaskCount = Math.Min(_displayTasks.Count, 1 + (int)Math.Floor(
            Math.Max(0, availableHeight - fixedHeight + 0.01) / SecondaryTaskRowHeight));
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
            // A finished task needs no wording: the muted dot already says it, and the spare width
            // goes to the title and model. Other states keep their text because it needs reading.
            var finished = task.Status == "completed";
            var status = new TextBlock
            {
                // Rows are narrow; the compact status keeps the title readable while the tooltip and
                // the primary badge still carry the full wording.
                Text = CompactStatus(task.Status), Foreground = color, FontSize = (waitingForUser ? 7.5 : 6.5) * _fontScale,
                FontWeight = waitingForUser ? FontWeights.Bold : FontWeights.Normal,
                Visibility = finished ? Visibility.Collapsed : Visibility.Visible,
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
        ? !string.IsNullOrWhiteSpace(task.OpenUri) ||
          !string.IsNullOrWhiteSpace(task.ProjectPath) && Directory.Exists(task.ProjectPath)
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
                // Desktop conversations expose one app-level deep link, so focus the running app
                // instead of relying on the protocol handler to pop it to the front.
                if (!string.IsNullOrWhiteSpace(task.OpenUri))
                {
                    e.Handled = true;
                    _dockHideTimer.Stop();
                    if (DeepSeekDesktopApp.Activate(task.OpenUri)) return;
                    throw new InvalidOperationException(
                        LocalizationService.L("DeepSeek 桌面版未响应。", "The DeepSeek desktop app did not respond."));
                }
                // Deep Code sessions expose the project folder they belong to instead.
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

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateFooterHeight();
        RefreshVisibleTaskRows();
    }

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
        _headerError = ex;
        RenderHeaderError(ex);
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
        _ageTimer.Stop();
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
            _ageTimer.Start();
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
        if (_lastSnapshot is { } snapshot)
        {
            RenderQuotaBand(snapshot, resizeWindow: true);
            RenderSnapshot(snapshot);
        }
        _ = RefreshAsync();
    }

    private async void HeaderStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var settings = _service.ReloadSettings();
        settings.FloatingHeaderStyle = FloatingHeaderStyles.Normalize(item.Tag?.ToString());
        _service.SaveSettings(settings);
        UpdateContextMenu();
        if (_lastSnapshot is { } snapshot) RenderSnapshot(snapshot);
        // Usage-based styles need the local/official usage the service loads lazily.
        if (IsLoaded && !_isDockCollapsed) await RefreshAsync();
    }

    private void FloatingMenu_Opened(object sender, RoutedEventArgs e) => UpdateContextMenu();

    private void FooterDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var settings = _service.ReloadSettings();
        switch (item.Tag?.ToString())
        {
            case "visible": settings.FloatingFooterVisible = item.IsChecked; break;
            case "fiveHour": settings.FloatingShowFiveHourQuota = item.IsChecked; break;
            case "weekly": settings.FloatingShowWeeklyQuota = item.IsChecked; break;
            case "deepSeek": settings.FloatingShowDeepSeekQuota = item.IsChecked; break;
            default: return;
        }
        _service.SaveSettings(settings);
        UpdateContextMenu();
        if (_lastSnapshot is { } snapshot)
        {
            RenderQuotaBand(snapshot, resizeWindow: true);
            RenderSnapshot(snapshot);
        }
        else UpdateFooterLayout(resizeWindow: true);
        if (IsLoaded) SavePlacement();
    }

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
        FooterDisplayMenuItem.Header = english ? "Footer display" : "底栏显示";
        FooterVisibleMenuItem.Header = english ? "Show footer" : "显示底栏";
        FooterFiveHourMenuItem.Header = english ? "Codex · 5h left" : "Codex · 5h 剩余";
        FooterWeeklyMenuItem.Header = english ? "Codex · Week left" : "Codex · 周剩余";
        FooterDeepSeekMenuItem.Header = english ? "DeepSeek · Balance / plan" : "DeepSeek · 余额 / 套餐";
        FooterVisibleMenuItem.IsChecked = _service.Settings.FloatingFooterVisible;
        FooterFiveHourMenuItem.IsChecked = _service.Settings.FloatingShowFiveHourQuota;
        FooterWeeklyMenuItem.IsChecked = _service.Settings.FloatingShowWeeklyQuota;
        FooterDeepSeekMenuItem.IsChecked = _service.Settings.FloatingShowDeepSeekQuota;
        HeaderStyleMenuItem.Header = english ? "Header text" : "顶栏显示";
        var headerStyle = FloatingHeaderStyles.Normalize(_service.Settings.FloatingHeaderStyle);
        foreach (var item in HeaderStyleMenuItem.Items.OfType<MenuItem>())
        {
            item.Header = HeaderStyleCaption(item.Tag?.ToString() ?? string.Empty);
            item.IsChecked = FloatingHeaderStyles.Normalize(item.Tag?.ToString()) == headerStyle;
        }
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
        _resetBannerHeight = height;
        UpdateMinimumHeight(resizeWindow: true);
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
