using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        if (args.Contains("--account-reset-tests", StringComparer.OrdinalIgnoreCase))
        {
            VerifyAccountResetRecovery();
            return 0;
        }
        if (args.Contains("--shared-usage-tests", StringComparer.OrdinalIgnoreCase))
        {
            VerifySharedUsageCounter();
            return 0;
        }
        if (args.Contains("--reset-tests", StringComparer.OrdinalIgnoreCase))
        {
            VerifyResetSignals(args);
            return 0;
        }
        var app = new App();
        app.InitializeComponent();
        LocalizationService.Initialize(LocalizationService.English);
        if (args.Contains("--reset-attention", StringComparer.OrdinalIgnoreCase))
        {
            VerifyResetAttention(args);
            app.ExitApplication();
            return 0;
        }
        if (args.Contains("--floating-restore", StringComparer.OrdinalIgnoreCase))
        {
            VerifyFloatingWindowRestore(app);
            return 0;
        }
        if (args.Contains("--dashboard-pages", StringComparer.OrdinalIgnoreCase))
        {
            VerifyDashboardPages(new AiManagerWindow());
            app.ExitApplication();
            return 0;
        }
        if (args.Contains("--capture-pages", StringComparer.OrdinalIgnoreCase))
        {
            VerifyDashboardRedesign(args);
            app.ExitApplication();
            return 0;
        }
        if (args.Contains("--card-layout", StringComparer.OrdinalIgnoreCase))
        {
            VerifyDashboardCardContent(args);
            app.ExitApplication();
            return 0;
        }
        if (args.Contains("--quota-notifications", StringComparer.OrdinalIgnoreCase))
        {
            VerifyQuotaNotifications();
            app.ExitApplication();
            return 0;
        }
        VerifyQuotaNotifications();
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
        Check(GitHubUpdateChecker.BuildLatestReleasePageUrl("PN-BUG", "AI-Master") ==
              "https://github.com/PN-BUG/AI-Master/releases/latest",
            "update checker has a non-API fallback for GitHub rate limits");
        var updateAssets = new List<GitHubReleaseAsset>
        {
            new("AIMaster-win-x64-lightweight.zip", "https://github.com/example/lightweight", 100, null),
            new("AIMaster-win-x64-standalone.zip", "https://github.com/example/standalone", 200, null)
        };
        Check(ApplicationUpdater.SelectAsset(updateAssets, "AIMaster", "win-x64", "lightweight")?.SizeBytes == 100,
            "self-updater selects the matching lightweight package");
        Check(ApplicationUpdater.SelectAsset(updateAssets, "AIMaster", "win-x64", "standalone")?.SizeBytes == 200,
            "self-updater selects the matching standalone package");
        VerifyUpdaterPreparation();
        VerifyOneClickPackager();
        VerifyBilingualDocumentation();
        var parsedRelease = GitHubUpdateChecker.ParseReleaseJson(
            "{\"tag_name\":\"v1.2.0\",\"html_url\":\"https://github.com/PN-BUG/AI-Master/releases/tag/v1.2.0\",\"body\":\"## Changes\\nA new feature\",\"assets\":[{\"name\":\"AIMaster-win-x64-lightweight.zip\",\"browser_download_url\":\"https://github.com/PN-BUG/AI-Master/releases/download/v1.2.0/AIMaster-win-x64-lightweight.zip\",\"size\":123,\"digest\":\"sha256:abcd\"}]}",
            "1.0.0", "PN-BUG", "AI-Master");
        Check(parsedRelease.UpdateAvailable && parsedRelease.ReleaseAssets.Count == 1 &&
              parsedRelease.ReleaseNotes == "## Changes\nA new feature" &&
              parsedRelease.ReleaseAssets[0].Digest == "sha256:abcd",
            "update checker parses release notes, assets, and SHA-256 digests");
        var fallbackRelease = GitHubUpdateChecker.ParseLatestReleasePageUri(
            new Uri("https://github.com/PN-BUG/AI-Master/releases/tag/v1.2.0"),
            "1.0.0", "PN-BUG", "AI-Master",
            ["AIMaster-win-x64-lightweight.zip", "AIMaster-win-x64-standalone.zip"]);
        Check(fallbackRelease.UpdateAvailable && fallbackRelease.LatestVersion == "v1.2.0" &&
              fallbackRelease.ReleaseAssets.Count == 2 &&
              fallbackRelease.ReleaseAssets[0].DownloadUrl ==
              "https://github.com/PN-BUG/AI-Master/releases/download/v1.2.0/AIMaster-win-x64-lightweight.zip",
            "rate-limit fallback resolves the latest tag and direct-update packages");
        VerifySharedUsageContract();
        using (var protocolValues = System.Text.Json.JsonDocument.Parse(
                   "{\"requestId\":\"42\",\"usedPercent\":\"12.5\",\"tokens\":\"123456\"}"))
        {
            var protocolRoot = protocolValues.RootElement;
            using var numericResponse = System.Text.Json.JsonDocument.Parse("{\"id\":\"42\"}");
            using var serverRequest = System.Text.Json.JsonDocument.Parse(
                "{\"id\":\"server-request\",\"method\":\"item/tool/call\"}");
            Check(CodexAppServerClient.TryReadResponseId(
                      numericResponse.RootElement, out var requestId) &&
                  requestId == 42 &&
                  JsonProtocolValue.TryGetDouble(protocolRoot.GetProperty("usedPercent"), out var usedPercent) &&
                  Math.Abs(usedPercent - 12.5) < 0.001 &&
                  JsonProtocolValue.TryGetInt64(protocolRoot.GetProperty("tokens"), out var tokens) &&
                  tokens == 123456,
                "Codex protocol accepts numeric values encoded as strings");
            Check(!CodexAppServerClient.TryReadResponseId(
                    serverRequest.RootElement, out _),
                "Codex server requests with string IDs do not tear down the response reader");
        }

        var main = new AiManagerWindow();
        var floating = new AiFloatingWindow();
        app.InitializeTrayIcon();

        Check(app.IsTrayIconVisible, "AIMaster creates a visible Windows tray icon");
        Check(main.ShowInTaskbar, "main window is shown in the taskbar");
        Check(!floating.ShowInTaskbar, "floating window stays out of the taskbar");
        Check(main.FindName("CheckUpdateButton") is Button updateButton &&
              Equals(updateButton.Content, "⇩ Check for updates"),
            "main window exposes the localized manual update check");
        Check(main.FindName("ViewUpdateButton") is Button notesButton &&
              Equals(notesButton.Content, "What's new") && notesButton.Visibility == Visibility.Collapsed,
            "release notes action appears after finding a release");
        Check(main.FindName("InstallUpdateButton") is Button installButton &&
              Equals(installButton.Content, "⇩ Update now") && installButton.Visibility == Visibility.Collapsed,
            "main window exposes the localized direct-update action on demand");
        Check(main.FindName("SharedUsageServerUrlBox") is TextBox { Text: "https://www.woliux.com" } &&
              main.FindName("SharedUsageSyncKeyBox") is PasswordBox,
            "shared usage card exposes the deployed woliu endpoint and a masked sync key");
        Check(main.FindName("AccountForecastButton") is Button { Content: "Account" } &&
              main.FindName("LocalForecastButton") is Button { Content: "This device" },
            "forecast card exposes account and local scope controls");
        Check(main.FindName("ThemeButton") is Button themeButton &&
              (Equals(themeButton.Content, "☀ Light") || Equals(themeButton.Content, "☾ Dark")),
            "main window exposes the day/night theme control");
        Check(main.Icon != null, "main window has the AIMaster icon");
        Check(floating.Icon != null, "floating window has the AIMaster icon");
        Check(main.FindName("DashboardScrollViewer") is ScrollViewer
              { VerticalScrollBarVisibility: ScrollBarVisibility.Auto },
            "dashboard scrolling uses the themed scrollbar when needed");
        var inlineHost = new TextBlock();
        var inlineRun = new System.Windows.Documents.Run("Drag handle");
        inlineHost.Inlines.Add(inlineRun);
        Check(ReferenceEquals(AiManagerWindow.FindAncestor<TextBlock>(inlineRun), inlineHost),
            "dashboard drag handles content-element mouse sources without crashing");
        VerifyDashboardLayout();
        VerifyDashboardPages(main);
        VerifyDashboardCardResize(main);
        VerifyProjectUsageTemplate(main);
        VerifySharedUsageTemplate(main);
        var shell = (Border)floating.FindName("Shell");
        Check(shell.Effect is null, "floating window has no outer shadow");
        var peekSignal = (System.Windows.Shapes.Rectangle)floating.FindName("PeekSignal");
        Check(peekSignal.Effect is null, "collapsed handle has no glow shadow");
        Check(new AiManagerSettings().TaskNameSource == TaskNameSources.ConversationTitle,
            "conversation title is the default task-name source");
        Check(new AiManagerSettings().Theme == ThemeModes.Dark &&
              ThemeModes.Normalize("LIGHT") == ThemeModes.Light &&
              ThemeModes.Normalize("unknown") == ThemeModes.Dark,
            "theme settings default safely and normalize persisted values");
        var themeResources = new ResourceDictionary();
        ThemeService.Apply(themeResources, ThemeModes.Light);
        Check(themeResources["AppBackground"] is System.Windows.Media.SolidColorBrush lightBackground &&
              lightBackground.Color.R > 230 &&
              themeResources["Ink"] is System.Windows.Media.SolidColorBrush lightInk &&
              lightInk.Color.R < 40,
            "light theme supplies a bright surface and high-contrast text");
        Check(Math.Abs(new AiManagerSettings().FloatingFontScale - 1.2) < 0.001,
            "floating text is larger by default");
        Check(floating.FindName("TaskNameText") is TextBlock { FontSize: >= 11.9 },
            "floating task text applies the larger default font scale");
        Check(floating.MinHeight == 98 && double.IsPositiveInfinity(floating.MaxWidth) &&
              Descendants(floating).OfType<Thumb>().Count(item => Equals(item.Style, floating.FindResource("ResizeThumb"))) == 8,
            "floating window exposes eight-direction resize handles without an artificial width cap");
        Check(AiFloatingWindow.CalculateVisibleTaskCount(98, 8) == 1 &&
              AiFloatingWindow.CalculateVisibleTaskCount(116, 8) == 2 &&
              AiFloatingWindow.CalculateVisibleTaskCount(224, 8) == 8,
            "floating task count follows the available window height");
        Check(AiFloatingWindow.BuildTaskUri("0199c8a0-0a12-7000-8000-0123456789ab")?.AbsoluteUri ==
              "codex://threads/0199c8a0-0a12-7000-8000-0123456789ab" &&
              AiFloatingWindow.BuildTaskUri("not-a-thread-id") is null,
            "floating task links target the Codex desktop protocol only for valid thread IDs");
        var savedPlacement = new AiFloatingWindowPlacement
            { Left = -1850, Top = 50, Width = 300, Height = 150 };
        var restoredPlacement = JsonSerializer.Deserialize<AiManagerSettings>(
            JsonSerializer.Serialize(new AiManagerSettings { FloatingWindowPlacement = savedPlacement }))?
            .FloatingWindowPlacement;
        var fittedPlacement = AiFloatingWindow.FitPlacementToWorkArea(restoredPlacement,
            new Rect(-1920, 0, 1920, 1080), 180, 98);
        var movedMonitorPlacement = AiFloatingWindow.FitPlacementToWorkArea(restoredPlacement,
            new Rect(0, 0, 1920, 1080), 180, 98);
        Check(fittedPlacement is { } fitted && fitted.Left == -1850 && fitted.Top == 50 &&
              fitted.Width == 300 && fitted.Height == 150 &&
              movedMonitorPlacement is { } clamped && clamped.Left == 0 &&
              AiFloatingWindow.FitPlacementToWorkArea(new AiFloatingWindowPlacement
                  { Left = double.NaN, Top = 0, Width = 300, Height = 150 },
                  new Rect(0, 0, 1920, 1080), 180, 98) is null,
            "floating placement survives settings serialization and stays visible when monitors change");
        VerifyTaskOrdering();
        Check(Math.Abs(FloatingFontScales.Normalize(9) - 1.5) < 0.001,
            "floating font scale is clamped to its supported range");
        VerifyWeeklyRemainingForecast();
        VerifyLocalForecast();
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

    private static void VerifyUpdaterPreparation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"AIMaster-preparation-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new SelfUpdateOptions("AIMaster", "lightweight", false, true);
        try
        {
            var installation = Path.Combine(root, "installation with 'quote and spaces");
            Directory.CreateDirectory(installation);
            var executable = Path.Combine(installation, "AIMaster.exe");
            File.WriteAllText(executable, "old");
            Check(ApplicationUpdater.GetInstallationDirectory(executable) == installation,
                "installation directory is derived from the executable rather than the working directory or shortcuts");
            var archivePath = Path.Combine(root, "package.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("package\\AIMaster.exe").Open())) writer.Write("new");
            using (var input = File.OpenRead(archivePath))
            {
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
                ApplicationUpdater.VerifyDigest(new GitHubReleaseAsset("package.zip", "", 0, $"sha256:{digest}"), archivePath, options);
            }
            foreach (var invalidDigest in new[] { null, "sha256:bad", "md5:1234", "sha256:" + new string('0', 64) })
            {
                var rejected = false;
                try { ApplicationUpdater.VerifyDigest(new GitHubReleaseAsset("package.zip", "", 0, invalidDigest), archivePath, options); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "missing, malformed, unsupported, or mismatched digests cannot install a package");
            }
            var stageRoot = Path.Combine(root, "extracted");
            ApplicationUpdater.ExtractArchiveSafely(archivePath, stageRoot, options);
            var stagedDirectory = Path.Combine(stageRoot, "package");
            Check(File.ReadAllText(Path.Combine(stagedDirectory, "AIMaster.exe")) == "new",
                "archive extraction accepts existing Windows release path separators");
            foreach (var invalidPath in new[] { "../escape.exe", "..\\escape.exe", "package/AIMaster.exe:stream", "package/../escape.exe", "package./escape.exe" })
            {
                var badArchive = Path.Combine(root, $"bad-{Guid.NewGuid():N}.zip");
                using (var archive = System.IO.Compression.ZipFile.Open(badArchive, System.IO.Compression.ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry(invalidPath).Open())) writer.Write("bad");
                var rejected = false;
                try { ApplicationUpdater.ExtractArchiveSafely(badArchive, Path.Combine(root, Guid.NewGuid().ToString("N")), options); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "archive traversal, alternate data streams, and ambiguous paths are rejected");
            }
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var scriptPath = Path.Combine(root, "generated-updater.ps1");
            var restartMock = """
                function Start-Process {
                    param($FilePath, $WorkingDirectory, $ArgumentList, [switch]$PassThru)
                    $process = New-Object PSObject -Property @{ HasExited = $false; ExitCode = 0 }
                    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { return $false }
                    return $process
                }
                """;
            File.WriteAllText(scriptPath, restartMock + "\n" + ApplicationUpdater.BuildUpdaterScript(int.MaxValue, 0,
                stagedDirectory, executable, workspace, Path.Combine(root, "logs"), options), new UTF8Encoding(true));
            var startInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath }) startInfo.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            if (!process.WaitForExit(15000)) { process.Kill(); throw new InvalidOperationException("Generated updater did not finish."); }
            if (process.ExitCode != 0 || File.ReadAllText(executable) != "new")
            {
                var errorPath = Path.Combine(workspace, "error.txt");
                throw new InvalidOperationException($"Generated updater failed ({process.ExitCode}): " +
                    (File.Exists(errorPath) ? File.ReadAllText(errorPath) : File.ReadAllText(scriptPath)));
            }
            Check(process.ExitCode == 0 && File.ReadAllText(executable) == "new",
                "the generated embedded updater runs successfully with quoted installation paths");
        }
        finally { Directory.Delete(root, recursive: true); }
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
              commandScript.Contains("package-and-restart.ps1\"", StringComparison.Ordinal) &&
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
        Check(normalized.Count == 10 && normalized[0] == "right:quota" &&
              normalized[1] == "right:forecast" && normalized.Select(item => item.Split(':')[1]).Distinct().Count() == 10,
            "dashboard layout preserves valid card moves and restores every missing card once");
    }

    private static void VerifyDashboardCardResize(AiManagerWindow main)
    {
        var resizeThumbs = Descendants(main).OfType<Thumb>()
            .Where(item => Equals(item.Style, main.FindResource("CardResizeThumb")))
            .ToList();
        var quotaCard = (Border)main.FindName("QuotaCard");
        Check(resizeThumbs.Count == 10 && resizeThumbs.All(item => Grid.GetRow(item) == 0 && Grid.GetRowSpan(item) == 2 &&
                  item.HorizontalAlignment == HorizontalAlignment.Stretch && item.Height == 8) &&
              main.FindName("DailyUsageCanvas") is Canvas &&
              double.IsNaN(quotaCard.Width) && quotaCard.HorizontalAlignment == HorizontalAlignment.Stretch,
            "dashboard cards keep fluid widths and expose an invisible full-width bottom resize edge");
        var padding = new Thickness(12);
        var size = new Size(300, 200);
        Check(AiManagerWindow.IsCardMoveEdge(new Point(2, 70), size, padding, false) &&
              AiManagerWindow.IsCardMoveEdge(new Point(150, 2), size, padding, false) &&
              !AiManagerWindow.IsCardMoveEdge(new Point(150, 70), size, padding, false) &&
              !AiManagerWindow.IsCardMoveEdge(new Point(2, 197), size, padding, false) &&
              !AiManagerWindow.IsCardMoveEdge(new Point(-2, 70), size, padding, false) &&
              AiManagerWindow.IsCardMoveEdge(new Point(150, 197), size, padding, true),
            "card move edges leave content and the expanded bottom resize edge available");
        var hints = Descendants(main).OfType<TextBlock>().Where(item => Equals(item.Tag, "CardHelp")).ToList();
        var helpButtons = Descendants(main).OfType<Button>().Where(item => Equals(item.Content, "?")).ToList();
        Check(hints.Count == 11 && hints.All(item => item.Visibility == Visibility.Collapsed) && helpButtons.Count == 10,
            "card explanations are hidden from the page and available through help buttons");
        var scopeHint = (TextBlock)main.FindName("ForecastScopeNoteText");
        var forecastCard = (Border)main.FindName("ForecastCard");
        var scopeHelp = Descendants(forecastCard).OfType<Button>().Where(item => Equals(item.Content, "?"))
            .Select(item => (ToolTip)item.ToolTip).SelectMany(Descendants).OfType<TextBlock>()
            .Single(item => ReferenceEquals(System.Windows.Data.BindingOperations.GetBinding(item, TextBlock.TextProperty)?.Source, scopeHint));
        var previousHint = scopeHint.Text;
        scopeHint.Text = "Updated scope";
        Check(scopeHelp.Text == "Updated scope", "help text follows live scope updates");
        scopeHint.Text = previousHint;
        quotaCard.Measure(new Size(420, double.PositiveInfinity));
        quotaCard.Arrange(new Rect(0, 0, 420, quotaCard.DesiredSize.Height));
        quotaCard.UpdateLayout();
        // The summary switches to a vertical arrangement after receiving its first width.
        quotaCard.Measure(new Size(420, double.PositiveInfinity));
        quotaCard.Arrange(new Rect(0, 0, 420, quotaCard.DesiredSize.Height));
        quotaCard.UpdateLayout();
        // The summary switches to a vertical arrangement after receiving its first width.
        quotaCard.Measure(new Size(420, double.PositiveInfinity));
        quotaCard.Arrange(new Rect(0, 0, 420, quotaCard.DesiredSize.Height));
        quotaCard.UpdateLayout();
        var bottomHit = System.Windows.Media.VisualTreeHelper.HitTest(quotaCard,
            new Point(quotaCard.ActualWidth / 2, quotaCard.ActualHeight - 4))?.VisualHit;
        Check(AiManagerWindow.FindAncestor<Thumb>(bottomHit) is { } bottomThumb &&
              Equals(bottomThumb.Style, main.FindResource("CardResizeThumb")),
            "the invisible bottom resize edge is reachable in the arranged card");
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
            ProjectName = "Smoke", Tokens = 1_000, SharePercent = 25, WeeklyQuotaPercent = 10,
            MostUsedModel = "gpt-5.6-terra"
        };
        row.Measure(new Size(420, 80));
        row.Arrange(new Rect(0, 0, 420, 80));
        row.UpdateLayout();
        Check(true, "project usage card creates its quota progress binding without a runtime exception");
    }

    private static void VerifyTaskOrdering()
    {
        var now = DateTimeOffset.Now;
        var ordered = AiManagerService.SortTasksByStatusAndTime([
            new AiThreadSummary { Id = "complete-new", Status = "completed", UpdatedAt = now },
            new AiThreadSummary { Id = "running-old", Status = "inProgress", UpdatedAt = now.AddMinutes(-2) },
            new AiThreadSummary { Id = "waiting", Status = "waitingOnUserInput", UpdatedAt = now.AddMinutes(-10) },
            new AiThreadSummary { Id = "running-new", Status = "active", UpdatedAt = now.AddMinutes(-1) },
            new AiThreadSummary { Id = "complete-old", Status = "completed", UpdatedAt = now.AddMinutes(-5) }
        ]).Select(item => item.Id).ToArray();

        Check(ordered.SequenceEqual(["waiting", "running-new", "running-old", "complete-new", "complete-old"]),
            "tasks sort by actionable status first and newest update first within each status group");
    }

    private static void VerifySharedUsageContract()
    {
        Check(SharedUsageClient.TryBuildSyncEndpoint("https://www.woliux.com", out var endpoint, out _) &&
              endpoint!.AbsoluteUri == "https://www.woliux.com/api/v1/aimaster/sync" &&
              !SharedUsageClient.TryBuildSyncEndpoint("http://www.woliux.com", out _, out _) &&
              SharedUsageClient.TryBuildSyncEndpoint("http://localhost:3000", out _, out _),
            "shared usage requires HTTPS publicly and targets the deployed woliu API");

        var secret = LocalSecretProtection.GenerateSyncKey();
        var protectedSecret = LocalSecretProtection.Protect(secret);
        var settingsJson = System.Text.Json.JsonSerializer.Serialize(new AiManagerSettings
        {
            SharedUsageSyncKey = secret,
            SharedUsageSyncKeyProtected = protectedSecret
        });
        Check(secret.Length >= 32 && protectedSecret != secret &&
              LocalSecretProtection.Unprotect(protectedSecret) == secret &&
              !settingsJson.Contains(secret, StringComparison.Ordinal),
            "shared usage sync keys are high entropy and protected by Windows DPAPI");

        var json = SharedUsageClient.SerializeUpload(new AiManagerSettings
        {
            SharedUsageDeviceId = "8b38a721-400c-4d80-a344-17bfa725f542",
            SharedUsageDeviceName = "Smoke PC"
        }, new AiLocalUsageSummary
        {
            PeriodStart = new DateOnly(2026, 9, 14),
            PeriodEnd = new DateOnly(2026, 9, 20),
            TotalTokens = 12_345,
            SessionCount = 7,
            MostUsedModel = "gpt-5.6-terra",
            Projects = [new AiProjectUsage { ProjectName = "Private project", ProjectPath = @"C:\Private" }]
        });
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var fields = document.RootElement.EnumerateObject().Select(item => item.Name).ToHashSet();
        Check(fields.SetEquals([
                  "deviceId", "deviceName", "periodStart", "periodEnd", "totalTokens", "sessionCount",
                  "mostUsedModel", "capturedAt"
              ]) && !json.Contains("Private", StringComparison.Ordinal),
            "shared upload contains only aggregate fields and excludes project data");
    }

    private static void VerifySharedUsageTemplate(AiManagerWindow main)
    {
        var items = (ItemsControl)main.FindName("SharedDeviceItems");
        var row = (FrameworkElement)items.ItemTemplate.LoadContent();
        row.DataContext = new AiDeviceUsage
        {
            DeviceId = "8b38a721-400c-4d80-a344-17bfa725f542",
            DeviceName = "Smoke PC",
            TotalTokens = 10_000,
            TokenSharePercent = 62.5,
            SessionCount = 4,
            MostUsedModel = "gpt-5.6-terra",
            UpdatedAt = DateTimeOffset.Now,
            IsCurrentDevice = true
        };
        row.Measure(new Size(520, 100));
        row.Arrange(new Rect(0, 0, 520, 100));
        row.UpdateLayout();
        Check(true, "shared device row creates its usage and localization bindings without a runtime exception");
    }

    private static void VerifyLocalTaskNameSources()
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path,
        [
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"smoke-thread\",\"thread_source\":\"user\"}}",
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"First request\"}]}}",
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"# Files mentioned by the user:\\nfile.png\\n\\n## My request:\\nLatest request <image name=[Image #1] path='C:/Temp/attachment.png'></image>\"}]}}",
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"<send_user_message_question_reply> [{\\\"questionItemId\\\":\\\"internal\\\"}]\"}]}}",
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

    private static void VerifyLocalForecast()
    {
        var capturedAt = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
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
                    ResetsAt = capturedAt.AddDays(4),
                    IsPrimary = true
                }
            ],
            DailyUsage =
            [
                new AiDailyUsage { Date = new DateOnly(2026, 9, 14), Tokens = 10_000 },
                new AiDailyUsage { Date = new DateOnly(2026, 9, 15), Tokens = 10_000 },
                new AiDailyUsage { Date = new DateOnly(2026, 9, 16), Tokens = 10_000 },
                new AiDailyUsage { Date = new DateOnly(2026, 9, 17), Tokens = 20_000 }
            ],
            LocalUsage = new AiLocalUsageSummary
            {
                PeriodStart = new DateOnly(2026, 9, 14),
                PeriodEnd = new DateOnly(2026, 9, 20),
                TotalTokens = 10_000,
                DailyUsage =
                [
                    new AiDailyUsage { Date = new DateOnly(2026, 9, 14), Tokens = 1_000 },
                    new AiDailyUsage { Date = new DateOnly(2026, 9, 15), Tokens = 2_000 },
                    new AiDailyUsage { Date = new DateOnly(2026, 9, 16), Tokens = 3_000 },
                    new AiDailyUsage { Date = new DateOnly(2026, 9, 17), Tokens = 4_000 }
                ]
            }
        };
        var service = new AiManagerService();
        var forecast = service.BuildLocalForecast(snapshot);
        Check(Math.Abs(forecast.DailyTokens - 10_000d / 7d) < 0.001 &&
              Math.Abs(forecast.DailyPercent - 8d / 3d) < 0.001 &&
              forecast.Summary.Contains("this device's current rate", StringComparison.Ordinal),
            "local forecast uses local tokens and this device's estimated share of account quota velocity");
        service.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void VerifyLocalProjectUsage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"AIMaster-Smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var todayDateTime = DateTime.Today;
        var previousTimestamp = new DateTimeOffset(todayDateTime.AddDays(-1).AddHours(9),
            TimeZoneInfo.Local.GetUtcOffset(todayDateTime.AddDays(-1).AddHours(9))).ToString("O");
        var morningTimestamp = new DateTimeOffset(todayDateTime.AddHours(10),
            TimeZoneInfo.Local.GetUtcOffset(todayDateTime.AddHours(10))).ToString("O");
        var afternoonTimestamp = new DateTimeOffset(todayDateTime.AddHours(15),
            TimeZoneInfo.Local.GetUtcOffset(todayDateTime.AddHours(15))).ToString("O");
        var alpha = Path.Combine(root, "alpha.jsonl");
        var beta = Path.Combine(root, "beta.jsonl");
        var gamma = Path.Combine(root, "gamma.jsonl");
        File.WriteAllLines(alpha,
        [
            $"{{\"timestamp\":\"{previousTimestamp}\",\"type\":\"session_meta\",\"payload\":{{\"cwd\":\"C:\\\\Work\\\\Alpha\"}}}}",
            $"{{\"timestamp\":\"{previousTimestamp}\",\"type\":\"turn_context\",\"payload\":{{\"model\":\"gpt-5.5\"}}}}",
            $"{{\"timestamp\":\"{previousTimestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"usage\":{{\"total_tokens\":1000}}}}}}",
            $"{{\"timestamp\":\"{previousTimestamp}\",\"type\":\"turn_context\",\"payload\":{{\"model\":\"gpt-5.6-sol\"}}}}",
            $"{{\"timestamp\":\"{previousTimestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"usage\":{{\"total_tokens\":2000}}}}}}"
        ]);
        File.WriteAllLines(beta,
        [
            $"{{\"timestamp\":\"{morningTimestamp}\",\"type\":\"turn_context\",\"payload\":{{\"cwd\":\"C:\\\\Work\\\\Beta\",\"model\":\"gpt-5.6-terra\"}}}}",
            $"{{\"timestamp\":\"{morningTimestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"usage\":{{\"total_tokens\":4000}}}}}}",
            $"{{\"timestamp\":\"{morningTimestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"total_tokens\":9000}},\"last_token_usage\":{{\"total_tokens\":500}}}}}}}}"
        ]);
        File.WriteAllLines(gamma,
        [
            $"{{\"timestamp\":\"{afternoonTimestamp}\",\"type\":\"turn_context\",\"payload\":{{\"cwd\":\"C:\\\\Work\\\\Gamma\",\"model\":\"gpt-5.6-terra\"}}}}",
            $"{{\"timestamp\":\"{afternoonTimestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"total_tokens\":500}},\"last_token_usage\":{{\"total_tokens\":500}}}}}}}}"
        ]);
        try
        {
            var service = new AiManagerService();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var first = service.ReadLocalUsageSummary(root, today.AddDays(-1));
            Check(first.TotalTokens == 7500 && first.SessionCount == 3 && first.Projects.Count == 3 &&
                  first.TodayTokens == 4500 && first.DailyUsage.Count == 7 &&
                  first.DailyUsage.Single(item => item.Date == today.AddDays(-1)).Tokens == 3000 &&
                  first.DailyUsage.Single(item => item.Date == today).Tokens == 4500 &&
                  first.TodayHourlyUsage.Count == 24 &&
                  first.TodayHourlyUsage.Single(item => item.Hour == 10).Tokens == 4000 &&
                  first.TodayHourlyUsage.Single(item => item.Hour == 15).Tokens == 500 &&
                  first.MostUsedModel == "gpt-5.6-terra" &&
                  first.Projects[0].ProjectName == "Beta" && first.Projects[0].Tokens == 4000 &&
                  first.Projects[0].MostUsedModel == "gpt-5.6-terra" &&
                  first.Projects.Single(item => item.ProjectName == "Alpha").MostUsedModel == "gpt-5.6-sol" &&
                  first.Projects.Single(item => item.ProjectName == "Gamma").Tokens == 500,
                "local usage groups by project, model, day, and local hour");

            File.AppendAllLines(beta,
            [
                $"{{\"timestamp\":\"{morningTimestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"usage\":{{\"total_tokens\":1000}}}}}}"
            ]);
            var second = service.ReadLocalUsageSummary(root, today.AddDays(-1));
            Check(second.TotalTokens == 8500 && second.TodayTokens == 5500 &&
                  second.TodayHourlyUsage.Single(item => item.Hour == 10).Tokens == 5000 &&
                  second.Projects.Single(item => item.ProjectName == "Beta").Tokens == 5000,
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
            PeriodStart = new DateOnly(2026, 9, 14),
            PeriodEnd = new DateOnly(2026, 9, 20),
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
        var accountUsage = new[]
        {
            new AiDailyUsage { Date = new DateOnly(2026, 9, 14), Tokens = 12_000 },
            new AiDailyUsage { Date = new DateOnly(2026, 9, 15), Tokens = 28_000 },
            new AiDailyUsage { Date = new DateOnly(2026, 9, 13), Tokens = 99_000 }
        };
        var estimate = AiManagerWindow.EstimateLocalQuotaUsage(usage, accountUsage, 40);
        var projects = AiManagerWindow.BuildProjectQuotaUsage(usage, estimate.EstimatedQuotaPercent);
        Check(estimate.AccountTokens == 40_000 &&
              Math.Abs(estimate.AccountTokenSharePercent!.Value - 25) < 0.001 &&
              Math.Abs(estimate.EstimatedQuotaPercent!.Value - 10) < 0.001 &&
              Math.Abs(projects[0].WeeklyQuotaPercent!.Value - 2.5) < 0.001 &&
              Math.Abs(projects[1].WeeklyQuotaPercent!.Value - 7.5) < 0.001,
            "local quota attribution first removes other devices, then distributes the device estimate by project");

        var todayUsage = new AiLocalUsageSummary
        {
            PeriodStart = usage.PeriodStart,
            PeriodEnd = usage.PeriodEnd,
            TotalTokens = usage.TotalTokens,
            TodayTokens = 4_000
        };
        Check(Math.Abs(AiManagerWindow.EstimateTodayWeeklyQuotaPercent(todayUsage, estimate)!.Value - 4) < 0.001,
            "today usage share uses the account's total weekly quota as the denominator");

        var incomplete = AiManagerWindow.EstimateLocalQuotaUsage(usage,
            [new AiDailyUsage { Date = new DateOnly(2026, 9, 14), Tokens = 5_000 }], 40);
        Check(incomplete.AccountTokenSharePercent is null && incomplete.EstimatedQuotaPercent is null,
            "incomplete account history never fabricates a per-device quota estimate");
        Check(AiManagerWindow.EstimateTodayWeeklyQuotaPercent(todayUsage, incomplete) is null,
            "today weekly-quota share remains unavailable when account history is incomplete");
    }

    [GeneratedRegex("[\\u3400-\\u9FFF]")]
    private static partial Regex HanCharacter();
}
