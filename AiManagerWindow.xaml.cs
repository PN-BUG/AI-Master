using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIMaster.Models;
using AIMaster.Services;

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

    public AiManagerWindow()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await RefreshAsync(showErrors: false);
        LocalizationService.LanguageChanged += LocalizationService_LanguageChanged;
        LocalizationService.Apply(this);
        UpdateLanguageButton();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadPolicyControls();
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(_service.Settings.RefreshSeconds, 30, 600));
        _timer.Start();
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

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(showErrors: true);

    private void FloatingWindow_Click(object sender, RoutedEventArgs e) => AiFloatingWindow.ShowOrActivate();

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
        RenderUsageBars(snapshot.DailyUsage);
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
            !TryReadPercent(PausePercentBox.Text, out var pause) || warning >= pause)
        {
            WpfMessageBox.Show(this,
                L("请输入 1–99 之间的百分比，并确保预警线低于暂停线。",
                    "Enter percentages from 1 to 99 and keep the warning limit below the pause limit."),
                L("策略无效", "Invalid guard policy"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = _service.ReloadSettings();
        settings.WarningPercent = warning;
        settings.PausePercent = pause;
        settings.AutoPause = AutoPauseCheck.IsChecked == true;
        _service.SaveSettings(settings);
        FooterStatusText.Text = L("保护策略已保存并应用", "Guard policy saved and applied");
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
        AutoPauseCheck.IsChecked = _service.Settings.AutoPause;
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

    internal static bool TryReadPercent(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value is >= 1 and <= 99;

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
    }
}
