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

    private static void CaptureLanguage(string language, string suffix, string outputDirectory)
    {
        LocalizationService.SetLanguage(language);
        var now = new DateTimeOffset(2026, 9, 14, 13, 35, 0, TimeSpan.FromHours(8));
        var managerSnapshot = CreateManagerSnapshot(now, LocalizationService.IsEnglish);
        var main = new AiManagerWindow();
        ResetDashboardLayout(main);
        Invoke(main, "RenderSnapshot", managerSnapshot);
        var mainContent = main.Content as FrameworkElement ?? main;
        Arrange(mainContent, 1180, 960);
        Invoke(main, "RenderRemainingForecastChart");
        SavePng(mainContent, 1180, 960,
            Path.Combine(outputDirectory, $"dashboard-{suffix}.png"));

        var floating = new AiFloatingWindow();
        Invoke(floating, "ApplyFontScale", 1.2d);
        Invoke(floating, "RenderSnapshot", CreateFloatingSnapshot(now, LocalizationService.IsEnglish));
        var floatingWidth = Math.Max(260, (int)Math.Ceiling(floating.Width));
        var floatingHeight = Math.Max(130, (int)Math.Ceiling(floating.Height));
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

    private static AiManagerSnapshot CreateManagerSnapshot(DateTimeOffset now, bool english)
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
                    Tokens = dailyTokens[index]
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
                TotalTokens = 3_765_000,
                SessionCount = 18,
                Projects =
                [
                    new AiProjectUsage { ProjectName = "AIMaster", ProjectPath = @"C:\Demo\AIMaster", Tokens = 2_070_750, SharePercent = 55, SessionCount = 9 },
                    new AiProjectUsage { ProjectName = english ? "Documentation" : "文档站", ProjectPath = @"C:\Demo\Docs", Tokens = 1_129_500, SharePercent = 30, SessionCount = 5 },
                    new AiProjectUsage { ProjectName = english ? "Prototype" : "原型项目", ProjectPath = @"C:\Demo\Prototype", Tokens = 564_750, SharePercent = 15, SessionCount = 4 }
                ]
            }
        };
    }

    private static AiFloatingSnapshot CreateFloatingSnapshot(DateTimeOffset now, bool english)
    {
        var tasks = new List<AiThreadSummary>
        {
            new() { Id = "docs-1", Title = english ? "Improve dashboard layout" : "优化仪表盘布局", Status = "inProgress", ModelName = "GPT-5", ReasoningEffort = "high", UpdatedAt = now },
            new() { Id = "docs-2", Title = english ? "Prepare v1.1 release" : "准备 v1.1 发布", Status = "completed", ModelName = "GPT-5", ReasoningEffort = "medium", UpdatedAt = now.AddMinutes(-18) },
            new() { Id = "docs-3", Title = english ? "Update bilingual docs" : "更新双语文档", Status = "waitingOnUserInput", ModelName = "GPT-5", ReasoningEffort = "medium", UpdatedAt = now.AddMinutes(-42) }
        };
        return new AiFloatingSnapshot
        {
            Tasks = tasks,
            TaskName = tasks[0].Title,
            TaskStatus = tasks[0].Status,
            TaskStatusText = tasks[0].StatusText,
            ModelName = "GPT-5",
            ReasoningEffort = "high",
            RemainingPercent = 63,
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
