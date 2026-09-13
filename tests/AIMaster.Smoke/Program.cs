using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        LocalizationService.Initialize(LocalizationService.English);
        Check(app.ShutdownMode == ShutdownMode.OnExplicitShutdown,
            "closing the last window does not terminate background monitoring");

        var main = new AiManagerWindow();
        var floating = new AiFloatingWindow();
        app.InitializeTrayIcon();

        Check(app.IsTrayIconVisible, "AIMaster creates a visible Windows tray icon");
        Check(main.ShowInTaskbar, "main window is shown in the taskbar");
        Check(!floating.ShowInTaskbar, "floating window stays out of the taskbar");
        Check(main.Icon != null, "main window has the AIMaster icon");
        Check(floating.Icon != null, "floating window has the AIMaster icon");
        var shell = (Border)floating.FindName("Shell");
        Check(shell.Effect is null, "floating window has no outer shadow");
        var peekSignal = (System.Windows.Shapes.Rectangle)floating.FindName("PeekSignal");
        Check(peekSignal.Effect is null, "collapsed handle has no glow shadow");
        Check(new AiManagerSettings().TaskNameSource == TaskNameSources.ConversationTitle,
            "conversation title is the default task-name source");
        Check(TaskNameSources.Normalize(TaskNameSources.LatestUserMessage) == TaskNameSources.LatestUserMessage,
            "latest sent message is a supported task-name source");
        VerifyLocalTaskNameSources();

        var uiText = CollectText(main)
            .Concat(CollectText(floating))
            .Concat(CollectMenuText(floating.ContextMenu))
            .Where(item => item.ElementName != "LanguageButton")
            .Where(item => HanCharacter().IsMatch(item.Text))
            .ToList();
        Check(uiText.Count == 0,
            uiText.Count == 0
                ? "English UI contains no untranslated built-in text"
                : $"untranslated UI text: {string.Join(" | ", uiText.Select(item => item.Text))}");

        var menuText = CollectMenuText(floating.ContextMenu).Select(item => item.Text).ToHashSet();
        Check(menuText.Contains("Task name") && menuText.Contains("Conversation title") &&
              menuText.Contains("Latest sent message"),
            "floating menu exposes both task-name choices in English");

        var limit = new AiLimitWindow
        {
            Name = "Codex 总额度",
            UsedPercent = 42,
            WindowDurationMinutes = 300,
            ResetsAt = DateTimeOffset.Now.AddHours(2)
        };
        var thread = new AiThreadSummary
        {
            Title = "未命名任务",
            Status = "inProgress",
            UpdatedAt = DateTimeOffset.Now
        };
        var dynamicText = new[]
        {
            limit.DisplayName, limit.WindowName, limit.UsedText, limit.RemainingText, limit.ResetText,
            thread.DisplayTitle, thread.StatusText, thread.UpdatedText
        };
        var untranslatedDynamicText = dynamicText.Where(text => HanCharacter().IsMatch(text)).ToList();
        Check(untranslatedDynamicText.Count == 0,
            untranslatedDynamicText.Count == 0
                ? "English quota, task status, reset time, and fallback text are localized"
                : $"untranslated dynamic text: {string.Join(" | ", untranslatedDynamicText)}");

        app.ExitApplication();
        Console.WriteLine("All AIMaster smoke checks passed.");
        return 0;
    }

    private static IEnumerable<(string ElementName, string Text)> CollectText(DependencyObject root)
    {
        if (root is FrameworkElement element)
        {
            if (element.ToolTip is string tooltip && !string.IsNullOrWhiteSpace(tooltip))
                yield return (element.Name, tooltip);
            if (element is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
                yield return (element.Name, textBlock.Text);
            if (element is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content))
                yield return (element.Name, content);
            if (element is HeaderedContentControl { Header: string header } && !string.IsNullOrWhiteSpace(header))
                yield return (element.Name, header);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var text in CollectText(child))
                yield return text;
    }

    private static IEnumerable<(string ElementName, string Text)> CollectMenuText(ContextMenu? menu)
    {
        if (menu is null) yield break;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Header is string header) yield return (item.Name, header);
            foreach (var nested in CollectMenuText(item)) yield return nested;
        }
    }

    private static IEnumerable<(string ElementName, string Text)> CollectMenuText(MenuItem parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (item.Header is string header) yield return (item.Name, header);
            foreach (var nested in CollectMenuText(item)) yield return nested;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS: {message}");
    }

    private static void VerifyLocalTaskNameSources()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path,
        [
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"smoke-thread\",\"thread_source\":\"user\"}}",
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"First request\"}]}}",
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"# Files mentioned by the user:\\nfile.png\\n\\n## My request:\\nLatest request\"}]}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}"
        ]);
        try
        {
            var service = new AiManagerService();
            service.Settings.TaskNameSource = TaskNameSources.ConversationTitle;
            var titleTask = service.ReadLocalTask(path, DateTime.UtcNow);
            service.Settings.TaskNameSource = TaskNameSources.LatestUserMessage;
            var latestTask = service.ReadLocalTask(path, DateTime.UtcNow);
            Check(titleTask?.Title == "First request", "local conversation-title fallback uses the first user request");
            Check(latestTask?.Title == "Latest request", "local latest-message mode tracks the last real user request");
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [GeneratedRegex("[\\u3400-\\u9FFF]")]
    private static partial Regex HanCharacter();
}
