using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;
using SharedUpdates;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        LocalizationService.Initialize(LocalizationService.English);
        if (args.Contains("--capture-docs", StringComparer.OrdinalIgnoreCase))
        {
            var outputDirectory = args.SkipWhile(arg => !string.Equals(arg, "--output", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "docs", "images");
            DocumentationScreenshotCapture.Capture(outputDirectory);
            app.ExitApplication();
            return 0;
        }
        Check(app.ShutdownMode == ShutdownMode.OnExplicitShutdown,
            "closing the last window does not terminate background monitoring");
        Check(GitHubUpdateChecker.IsNewerVersion("v1.1.0", "1.0.0"),
            "update checker detects a newer semantic version");
        Check(!GitHubUpdateChecker.IsNewerVersion("v1.0.0", "1.0.0"),
            "update checker accepts an equal v-prefixed version");
        Check(GitHubUpdateChecker.BuildLatestReleaseApiUrl("PN-BUG", "AI-Master") ==
              "https://api.github.com/repos/PN-BUG/AI-Master/releases/latest",
            "update checker targets the AIMaster GitHub release feed");
        var updateAssets = new List<GitHubReleaseAsset>
        {
            new("AIMaster-win-x64-lightweight.zip", "https://github.com/example/lightweight", 100, null),
            new("AIMaster-win-x64-standalone.zip", "https://github.com/example/standalone", 200, null)
        };
        Check(ApplicationUpdater.SelectAsset(updateAssets, "AIMaster", "win-x64", "lightweight")?.SizeBytes == 100,
            "self-updater selects the matching lightweight package");
        Check(ApplicationUpdater.SelectAsset(updateAssets, "AIMaster", "win-x64", "standalone")?.SizeBytes == 200,
            "self-updater selects the matching standalone package");
        VerifyOneClickPackager();
        VerifyBilingualDocumentation();
        var parsedRelease = GitHubUpdateChecker.ParseReleaseJson(
            "{\"tag_name\":\"v1.2.0\",\"html_url\":\"https://github.com/PN-BUG/AI-Master/releases/tag/v1.2.0\",\"assets\":[{\"name\":\"AIMaster-win-x64-lightweight.zip\",\"browser_download_url\":\"https://github.com/PN-BUG/AI-Master/releases/download/v1.2.0/AIMaster-win-x64-lightweight.zip\",\"size\":123,\"digest\":\"sha256:abcd\"}]}",
            "1.0.0", "PN-BUG", "AI-Master");
        Check(parsedRelease.UpdateAvailable && parsedRelease.ReleaseAssets.Count == 1 &&
              parsedRelease.ReleaseAssets[0].Digest == "sha256:abcd",
            "update checker parses release assets and SHA-256 digests");

        var main = new AiManagerWindow();
        var floating = new AiFloatingWindow();
        app.InitializeTrayIcon();

        Check(app.IsTrayIconVisible, "AIMaster creates a visible Windows tray icon");
        Check(main.ShowInTaskbar, "main window is shown in the taskbar");
        Check(!floating.ShowInTaskbar, "floating window stays out of the taskbar");
        Check(main.FindName("CheckUpdateButton") is Button updateButton &&
              Equals(updateButton.Content, "⇩ Check for updates"),
            "main window exposes the localized manual update check");
        Check(main.FindName("InstallUpdateButton") is Button installButton &&
              Equals(installButton.Content, "⇩ Update now") && installButton.Visibility == Visibility.Collapsed,
            "main window exposes the localized direct-update action on demand");
        Check(main.Icon != null, "main window has the AIMaster icon");
        Check(floating.Icon != null, "floating window has the AIMaster icon");
        Check(main.FindName("DashboardScrollViewer") is ScrollViewer
              { VerticalScrollBarVisibility: ScrollBarVisibility.Hidden },
            "dashboard scrollbar is hidden while scrolling remains available");
        VerifyDashboardLayout();
        VerifyDashboardCardResize(main);
        VerifyProjectUsageTemplate(main);
        var shell = (Border)floating.FindName("Shell");
        Check(shell.Effect is null, "floating window has no outer shadow");
        var peekSignal = (System.Windows.Shapes.Rectangle)floating.FindName("PeekSignal");
        Check(peekSignal.Effect is null, "collapsed handle has no glow shadow");
        Check(new AiManagerSettings().TaskNameSource == TaskNameSources.ConversationTitle,
            "conversation title is the default task-name source");
        Check(Math.Abs(new AiManagerSettings().FloatingFontScale - 1.2) < 0.001,
            "floating text is larger by default");
        Check(floating.FindName("TaskNameText") is TextBlock { FontSize: >= 11.9 },
            "floating task text applies the larger default font scale");
        Check(Math.Abs(FloatingFontScales.Normalize(9) - 1.5) < 0.001,
            "floating font scale is clamped to its supported range");
        VerifyWeeklyRemainingForecast();
        VerifyLocalProjectUsage();
        VerifyProjectWeeklyQuotaShare();
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

    private static void VerifyOneClickPackager()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var buildScript = File.ReadAllText(Path.Combine(repositoryRoot, "build.ps1"));
        var commandScript = File.ReadAllText(Path.Combine(repositoryRoot, "package-all.cmd"));
        var startHereTemplate = File.ReadAllText(
            Path.Combine(repositoryRoot, "Resources", "START-HERE.template.txt"), Encoding.UTF8);
        Check(buildScript.Contains("[switch]$All", StringComparison.Ordinal) &&
              buildScript.Contains("AIMaster-$Runtime-standalone.zip", StringComparison.Ordinal) &&
              buildScript.Contains("AIMaster-$Runtime-lightweight.zip", StringComparison.Ordinal) &&
              commandScript.Contains("build.ps1\" -All", StringComparison.Ordinal) &&
              buildScript.Contains("UTF8Encoding($true)", StringComparison.Ordinal) &&
              startHereTemplate.Contains("安装并登录 Codex", StringComparison.Ordinal),
            "one-click packager targets both release folders and both ZIP archives");
    }

    private static void VerifyBilingualDocumentation()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var chineseReadme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        var englishReadme = File.ReadAllText(Path.Combine(repositoryRoot, "README.en.md"));
        var chineseGuide = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "USER-GUIDE.md"));
        var englishGuide = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "USER-GUIDE.en.md"));
        var imageDirectory = Path.Combine(repositoryRoot, "docs", "images");
        var imageNames = new[] { "dashboard-zh.png", "dashboard-en.png", "floating-zh.png", "floating-en.png" };
        var pngSignature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var validImages = imageNames.All(name =>
        {
            var path = Path.Combine(imageDirectory, name);
            return File.Exists(path) && new FileInfo(path).Length > 1_000 &&
                   File.ReadAllBytes(path).Take(pngSignature.Length).SequenceEqual(pngSignature);
        });
        Check(chineseReadme.Contains("README.en.md", StringComparison.Ordinal) &&
              englishReadme.Contains("README.md", StringComparison.Ordinal) &&
              chineseGuide.Contains("USER-GUIDE.en.md", StringComparison.Ordinal) &&
              englishGuide.Contains("USER-GUIDE.md", StringComparison.Ordinal) && validImages,
            "Chinese and English documentation cross-link four valid UI screenshots");
    }

    private static void VerifyDashboardLayout()
    {
        var normalized = AiManagerWindow.NormalizeDashboardLayout(
            ["right:quota", "left:quota", "left:unknown", "right:forecast"]);
        Check(normalized.Count == 7 && normalized[0] == "right:quota" &&
              normalized[1] == "right:forecast" && normalized.Select(item => item.Split(':')[1]).Distinct().Count() == 7,
            "dashboard layout preserves valid card moves and restores every missing card once");
    }

    private static void VerifyDashboardCardResize(AiManagerWindow main)
    {
        var resizeThumbs = Descendants(main).OfType<Thumb>().Count(item =>
            Equals(item.Style, main.FindResource("CardResizeThumb")));
        Check(resizeThumbs == 7, "every dashboard card exposes a resize handle");
        var normalized = AiManagerWindow.NormalizeDashboardCardSize(new AiDashboardCardSize
        {
            Width = 40,
            Height = 20
        });
        Check(normalized is { Width: 280, Height: 88 },
            "dashboard card sizes are constrained to usable minimums");
        Check(AiManagerWindow.NormalizeDashboardCardSize(new AiDashboardCardSize
        {
            Width = double.NaN,
            Height = 200
        }) is null, "invalid saved dashboard card sizes are ignored");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void VerifyProjectUsageTemplate(AiManagerWindow main)
    {
        var items = (ItemsControl)main.FindName("ProjectUsageItems");
        var row = (FrameworkElement)items.ItemTemplate.LoadContent();
        row.DataContext = new AiProjectUsage
        {
            ProjectName = "Smoke", Tokens = 1_000, SharePercent = 25, WeeklyQuotaPercent = 10
        };
        row.Measure(new Size(420, 80));
        row.Arrange(new Rect(0, 0, 420, 80));
        row.UpdateLayout();
        Check(true, "project usage card creates its quota progress binding without a runtime exception");
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

    private static void VerifyWeeklyRemainingForecast()
    {
        var capturedAt = new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(8));
        var snapshot = new AiManagerSnapshot
        {
            CapturedAt = capturedAt,
            Limits =
            [
                new AiLimitWindow
                {
                    Name = "Weekly",
                    UsedPercent = 40,
                    WindowDurationMinutes = 7 * 24 * 60,
                    IsPrimary = true
                }
            ]
        };
        var forecast = new AiForecast(0, 10, null, false, string.Empty);
        var points = AiManagerService.BuildWeeklyRemainingForecast(snapshot, forecast);
        Check(points.Count == 7 && points[0].Date == new DateOnly(2026, 9, 14) &&
              Math.Abs(points[0].RemainingPercent - 80) < 0.001 &&
              Math.Abs(points[2].RemainingPercent - 60) < 0.001 &&
              Math.Abs(points[6].RemainingPercent - 20) < 0.001,
            "weekly forecast spans Monday through Sunday around the current remaining quota");
        Check(!points[2].IsProjected && points[3].IsProjected,
            "weekly forecast separates estimated history from future projection");
    }

    private static void VerifyLocalProjectUsage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"AIMaster-Smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var timestamp = DateTimeOffset.Now.ToString("O");
        var alpha = Path.Combine(root, "alpha.jsonl");
        var beta = Path.Combine(root, "beta.jsonl");
        File.WriteAllLines(alpha,
        [
            $"{{\"timestamp\":\"{timestamp}\",\"type\":\"session_meta\",\"payload\":{{\"cwd\":\"C:\\\\Work\\\\Alpha\"}}}}",
            $"{{\"timestamp\":\"{timestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"usage\":{{\"total_tokens\":1000}}}}}}",
            $"{{\"timestamp\":\"{timestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"usage\":{{\"total_tokens\":2000}}}}}}"
        ]);
        File.WriteAllLines(beta,
        [
            $"{{\"timestamp\":\"{timestamp}\",\"type\":\"turn_context\",\"payload\":{{\"cwd\":\"C:\\\\Work\\\\Beta\"}}}}",
            $"{{\"timestamp\":\"{timestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"usage\":{{\"total_tokens\":4000}}}}}}"
        ]);
        try
        {
            var service = new AiManagerService();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var first = service.ReadLocalUsageSummary(root, today);
            Check(first.TotalTokens == 7000 && first.SessionCount == 2 && first.Projects.Count == 2 &&
                  first.Projects[0].ProjectName == "Beta" && first.Projects[0].Tokens == 4000,
                "local usage sums response tokens and groups them by project directory");

            File.AppendAllLines(beta,
            [
                $"{{\"timestamp\":\"{timestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"usage\":{{\"total_tokens\":1000}}}}}}"
            ]);
            var second = service.ReadLocalUsageSummary(root, today);
            Check(second.TotalTokens == 8000 && second.Projects.Single(item => item.ProjectName == "Beta").Tokens == 5000,
                "local usage incrementally reads appended token records without double counting");
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void VerifyProjectWeeklyQuotaShare()
    {
        var usage = new AiLocalUsageSummary
        {
            TotalTokens = 10_000,
            Projects =
            [
                new AiProjectUsage
                {
                    ProjectName = "Alpha", ProjectPath = @"C:\Work\Alpha",
                    Tokens = 2_500, SharePercent = 25
                },
                new AiProjectUsage
                {
                    ProjectName = "Beta", ProjectPath = @"C:\Work\Beta",
                    Tokens = 7_500, SharePercent = 75
                }
            ]
        };
        var projects = AiManagerWindow.BuildProjectQuotaUsage(usage, 40);
        Check(Math.Abs(projects[0].WeeklyQuotaPercent!.Value - 10) < 0.001 &&
              Math.Abs(projects[1].WeeklyQuotaPercent!.Value - 30) < 0.001,
            "project usage attributes the official weekly quota by local token share");
    }

    [GeneratedRegex("[\\u3400-\\u9FFF]")]
    private static partial Regex HanCharacter();
}
