using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMaster.Models;
using AIMaster.Services;

namespace AIMaster;

internal static class DocumentationScreenshotCapture
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Capture(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        CaptureLanguage(LocalizationService.Chinese, "zh", outputDirectory);
        CaptureLanguage(LocalizationService.English, "en", outputDirectory);
        Console.WriteLine($"Documentation screenshots written to {Path.GetFullPath(outputDirectory)}");
    }

    /// <summary>Renders every selectable floating-monitor header style for visual review.</summary>
    public static void CaptureHeaderStyles(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        LocalizationService.SetLanguage(LocalizationService.Chinese);
        var now = new DateTimeOffset(2026, 11, 14, 11, 30, 0, TimeSpan.FromHours(8));
        var workspace = Path.Combine(Path.GetTempPath(), "aimaster-header-styles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var service = new AiManagerService(settingsPath: Path.Combine(workspace, "settings.json"));
            var attention = new ResetSignalAttention(Path.Combine(workspace, "reset-attention.json"));
            var floating = new AiFloatingWindow(service, attention);
            floating.Width = 236;
            var snapshot = CreateHeaderStyleSnapshot(now);
            foreach (var style in new[]
                     {
                         FloatingHeaderStyles.QuotaReset, FloatingHeaderStyles.QuotaAlert,
                         FloatingHeaderStyles.TodayUsage, FloatingHeaderStyles.UsageTrend,
                         FloatingHeaderStyles.LatestEvent, FloatingHeaderStyles.Minimal,
                         FloatingHeaderStyles.Brand
                     })
            {
                service.Settings.FloatingHeaderStyle = style;
                Invoke(floating, "RenderSnapshot", snapshot);
                Render(floating.Content as FrameworkElement ?? floating, 236, 190,
                    Path.Combine(outputDirectory, $"floating-header-{style}.png"));
            }
            DetachLanguageChanged(floating);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"Header style screenshots written to {Path.GetFullPath(outputDirectory)}");
    }

    private static AiFloatingSnapshot CreateHeaderStyleSnapshot(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var tasks = new List<AiThreadSummary>
        {
            new() { Id = "demo-1", Title = "做一份抖音平台游戏的调研报告", Status = "inProgress",
                Source = AiProviderModes.DeepSeek, ModelName = "deepseek-v4.1-flash", UpdatedAt = now },
            new() { Id = "demo-2", Title = "ds 当前使用的是 opencode 的 go 套餐", Status = "inProgress",
                Source = AiProviderModes.DeepSeek, ModelName = "deepseek-v4.1-flash", UpdatedAt = now.AddMinutes(-3) },
            new() { Id = "demo-3", Title = "aimaster ds 任务状态显示有问题", Status = "inProgress",
                Source = AiProviderModes.DeepSeek, ModelName = "deepseek-v4.1-flash", UpdatedAt = now.AddMinutes(-9) },
            new() { Id = "demo-4", Title = "参考这个 bluemood.xiaomy.net 的页面", Status = "completed",
                Source = AiProviderModes.Codex, ModelName = "gpt-6.1-sol", UpdatedAt = now.AddMinutes(-26) },
            new() { Id = "demo-5", Title = "为什么有人注册验证成功，点击却失败", Status = "waitingOnUserInput",
                Source = AiProviderModes.Codex, ModelName = "gpt-6.1-sol", UpdatedAt = now.AddMinutes(-48) }
        };
        var fiveHour = new AiLimitWindow
        {
            LimitId = "codex", UsedPercent = 0, WindowDurationMinutes = 300, ResetsAt = now.AddHours(2)
        };
        var weekly = new AiLimitWindow
        {
            LimitId = "codex", UsedPercent = 99, WindowDurationMinutes = 10080, ResetsAt = now.AddHours(1.7)
        };
        return new AiFloatingSnapshot
        {
            Provider = AiProviderModes.DeepSeek,
            Tasks = tasks,
            TaskName = tasks[0].Title,
            TaskStatus = tasks[0].Status,
            TaskStatusText = tasks[0].StatusText,
            ModelName = tasks[0].ModelName ?? "deepseek-v4.1-flash",
            FiveHourQuota = fiveHour,
            WeeklyQuota = weekly,
            CodexFiveHourQuota = fiveHour,
            CodexWeeklyQuota = weekly,
            CodexQuotaCapturedAt = now.AddSeconds(-14),
            DeepSeekBalanceText = "¥0.06",
            DeepSeekBalance = new AiDeepSeekBalance { Total = 0.06 },
            DeepSeekBalanceCapturedAt = now.AddSeconds(-14),
            LocalCodexUsage = new AiLocalUsageSummary
            {
                PeriodStart = today.AddDays(-6),
                PeriodEnd = today,
                TotalTokens = 1_200_000,
                TodayTokens = 128_400,
                DailyUsage = Enumerable.Range(0, 7).Select(offset => new AiDailyUsage
                {
                    Date = today.AddDays(offset - 6),
                    Tokens = new long[] { 96_000, 210_000, 154_000, 320_000, 410_000, 248_000, 128_400 }[offset]
                }).ToList()
            },
            LocalUsageCapturedAt = now.AddSeconds(-3),
            CapturedAt = now
        };
    }

    private static void CaptureLanguage(string language, string suffix, string outputDirectory)
    {
        LocalizationService.SetLanguage(language);
        var now = new DateTimeOffset(2026, 9, 14, 13, 35, 0, TimeSpan.FromHours(8));
        var managerSnapshot = CreateManagerSnapshot(now, LocalizationService.IsEnglish);
        var main = new AiManagerWindow();
        ResetDashboardLayout(main);
        ((System.Windows.Controls.CheckBox)main.FindName("SharedUsageEnabledCheck")).IsChecked = true;
        ((System.Windows.Controls.TextBox)main.FindName("SharedUsageDeviceNameBox")).Text =
            LocalizationService.IsEnglish ? "Office PC" : "办公室电脑";
        ((System.Windows.Controls.PasswordBox)main.FindName("SharedUsageSyncKeyBox")).Password =
            "documentation-demo-sync-key-123456";
        Invoke(main, "RenderSnapshot", managerSnapshot);
        var mainContent = main.Content as FrameworkElement ?? main;
        Arrange(mainContent, 1180, 820);
        Invoke(main, "RenderRemainingForecastChart");
        Invoke(main, "RenderTodayUsageChart");
        Arrange(mainContent, 1180, 820);
        SavePng(mainContent, 1180, 820,
            Path.Combine(outputDirectory, $"dashboard-{suffix}.png"));

        var floating = new AiFloatingWindow();
        Invoke(floating, "ApplyFontScale", 1.2d);
        Invoke(floating, "RenderSnapshot", CreateFloatingSnapshot(now, LocalizationService.IsEnglish));
        var floatingWidth = Math.Max(200, (int)Math.Ceiling(floating.Width));
        var floatingHeight = Math.Max(186, (int)Math.Ceiling(floating.Height));
        Render(floating.Content as FrameworkElement ?? floating, floatingWidth, floatingHeight,
            Path.Combine(outputDirectory, $"floating-{suffix}.png"));
        DetachLanguageChanged(main);
        DetachLanguageChanged(floating);
    }

    private static void ResetDashboardLayout(AiManagerWindow main)
    {
        var serviceField = typeof(AiManagerWindow).GetField("_service", PrivateInstance)
                           ?? throw new MissingFieldException(typeof(AiManagerWindow).FullName, "_service");
        var service = (AiManagerService)(serviceField.GetValue(main)
                      ?? throw new InvalidOperationException("Dashboard service is unavailable."));
        service.Settings.DashboardCardLayout.Clear();
        service.Settings.CollapsedDashboardCards.Clear();
        service.Settings.DashboardCardSizes.Clear();
        Invoke(main, "ApplyDashboardLayout");
    }

    internal static AiManagerSnapshot CreateManagerSnapshot(DateTimeOffset now, bool english)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var dailyTokens = new long[] { 410_000, 525_000, 360_000, 690_000, 745_000, 580_000, 455_000 };
        return new AiManagerSnapshot
        {
            CapturedAt = now,
            LifetimeTokens = 28_640_000,
            ResetCredits = 2,
            Limits =
            [
                new AiLimitWindow
                {
                    LimitId = "weekly", Name = "Codex 总额度", UsedPercent = 37,
                    WindowDurationMinutes = 7 * 24 * 60, ResetsAt = now.AddDays(4), IsPrimary = true
                },
                new AiLimitWindow
                {
                    LimitId = "short", Name = "Codex", UsedPercent = 18,
                    WindowDurationMinutes = 5 * 60, ResetsAt = now.AddHours(3)
                }
            ],
            DailyUsage = Enumerable.Range(0, 7)
                .Select(index => new AiDailyUsage
                {
                    Date = today.AddDays(index - 6),
                    Tokens = dailyTokens[index] * 2
                }).ToList(),
            Threads =
            [
                new AiThreadSummary
                {
                    Id = "docs-1", Title = english ? "Improve dashboard layout" : "优化仪表盘布局",
                    Status = "inProgress", ModelName = "GPT-5", ReasoningEffort = "high", UpdatedAt = now
                },
                new AiThreadSummary
                {
                    Id = "docs-2", Title = english ? "Prepare v1.1 release" : "准备 v1.1 发布",
                    Status = "completed", ModelName = "GPT-5", ReasoningEffort = "medium", UpdatedAt = now.AddMinutes(-18)
                },
                new AiThreadSummary
                {
                    Id = "docs-3", Title = english ? "Update bilingual docs" : "更新双语文档",
                    Status = "waitingOnUserInput", ModelName = "GPT-5", ReasoningEffort = "medium", UpdatedAt = now.AddMinutes(-42)
                }
            ],
            LocalUsage = new AiLocalUsageSummary
            {
                PeriodStart = today.AddDays(-6),
                PeriodEnd = today,
                TotalTokens = 3_765_000,
                TodayTokens = 455_000,
                SessionCount = 18,
                MostUsedModel = "gpt-5.6-terra",
                TodayHourlyUsage =
                [
                    new AiHourlyUsage { Hour = 8, Tokens = 45_000 },
                    new AiHourlyUsage { Hour = 10, Tokens = 80_000 },
                    new AiHourlyUsage { Hour = 13, Tokens = 120_000 },
                    new AiHourlyUsage { Hour = 16, Tokens = 90_000 },
                    new AiHourlyUsage { Hour = 19, Tokens = 120_000 }
                ],
                Projects =
                [
                    new AiProjectUsage { ProjectName = "AIMaster", ProjectPath = @"C:\Demo\AIMaster", Tokens = 2_070_750, SharePercent = 55, SessionCount = 9, MostUsedModel = "gpt-5.6-terra" },
                    new AiProjectUsage { ProjectName = english ? "Documentation" : "文档站", ProjectPath = @"C:\Demo\Docs", Tokens = 1_129_500, SharePercent = 30, SessionCount = 5, MostUsedModel = "gpt-5.6-sol" },
                    new AiProjectUsage { ProjectName = english ? "Prototype" : "原型项目", ProjectPath = @"C:\Demo\Prototype", Tokens = 564_750, SharePercent = 15, SessionCount = 4, MostUsedModel = "gpt-5.5" }
                ]
            },
            SharedUsage = new AiSharedUsageSummary
            {
                Enabled = true,
                SyncedAt = now,
                TotalTokens = 6_120_000,
                Devices =
                [
                    new AiDeviceUsage
                    {
                        DeviceId = "8b38a721-400c-4d80-a344-17bfa725f542",
                        DeviceName = english ? "Office PC" : "办公室电脑",
                        TotalTokens = 3_765_000, TokenSharePercent = 61.52, SessionCount = 18,
                        MostUsedModel = "gpt-5.6-terra", UpdatedAt = now, IsCurrentDevice = true
                    },
                    new AiDeviceUsage
                    {
                        DeviceId = "21f74732-d677-49df-a72e-2d46b8007f24",
                        DeviceName = english ? "Home PC" : "家用电脑",
                        TotalTokens = 2_355_000, TokenSharePercent = 38.48, SessionCount = 11,
                        MostUsedModel = "gpt-5.6-sol", UpdatedAt = now.AddMinutes(-4)
                    }
                ]
            }
        };
    }

    private static AiFloatingSnapshot CreateFloatingSnapshot(DateTimeOffset now, bool english)
    {
        var tasks = new List<AiThreadSummary>
        {
            new() { Id = "docs-1", Title = english ? "Improve dashboard layout" : "优化仪表盘布局", Status = "inProgress", Source = AiProviderModes.Codex, ModelName = "GPT-5", ReasoningEffort = "high", UpdatedAt = now },
            new() { Id = "docs-2", Title = english ? "Prepare v1.1 release" : "准备 v1.1 发布", Status = "completed", Source = AiProviderModes.Codex, ModelName = "GPT-5", ReasoningEffort = "medium", UpdatedAt = now.AddMinutes(-18) },
            new() { Id = "docs-3", Title = english ? "Update bilingual docs" : "更新双语文档", Status = "waitingOnUserInput", Source = AiProviderModes.DeepSeek, ModelName = "deepseek-chat", ReasoningEffort = "medium", UpdatedAt = now.AddMinutes(-42) }
        };
        var fiveHour = new AiLimitWindow { LimitId = "codex", UsedPercent = 12,
            WindowDurationMinutes = 300, ResetsAt = now.AddHours(3) };
        var weekly = new AiLimitWindow { LimitId = "codex", UsedPercent = 37,
            WindowDurationMinutes = 10080, ResetsAt = now.AddDays(4) };
        return new AiFloatingSnapshot
        {
            Tasks = tasks,
            TaskName = tasks[0].Title,
            TaskStatus = tasks[0].Status,
            TaskStatusText = tasks[0].StatusText,
            ModelName = "GPT-5",
            ReasoningEffort = "high",
            RemainingPercent = 63,
            FiveHourQuota = fiveHour,
            WeeklyQuota = weekly,
            CodexFiveHourQuota = fiveHour,
            CodexWeeklyQuota = weekly,
            CodexQuotaCapturedAt = now,
            DeepSeekBalanceText = "¥5.39",
            DeepSeekBalanceDetail = english ? "Paid ¥3.05 · Granted ¥2.34" : "付费 ¥3.05 · 赠送 ¥2.34",
            DeepSeekBalanceCapturedAt = now,
            QuotaWindow = english ? "Codex total quota · 7-day window" : "Codex 总额度 · 7 天窗口",
            ResetsAt = now.AddDays(4),
            CapturedAt = now
        };
    }

    private static void Invoke(object target, string methodName, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, PrivateInstance)
                     ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        method.Invoke(target, arguments);
    }

    private static void DetachLanguageChanged(object target)
    {
        var handlerMethod = target.GetType().GetMethod("LocalizationService_LanguageChanged", PrivateInstance)
                            ?? throw new MissingMethodException(target.GetType().FullName,
                                "LocalizationService_LanguageChanged");
        var handler = (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), target, handlerMethod);
        typeof(LocalizationService).GetEvent(nameof(LocalizationService.LanguageChanged))
            ?.RemoveEventHandler(null, handler);
    }

    private static void Render(FrameworkElement element, int width, int height, string path)
    {
        Arrange(element, width, height);
        SavePng(element, width, height, path);
    }

    private static void Arrange(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void SavePng(FrameworkElement element, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"Created {Path.GetFileName(path)} ({width}x{height})");
    }
}
