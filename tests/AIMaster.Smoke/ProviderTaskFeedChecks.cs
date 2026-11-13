using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyProviderTaskFeed()
    {
        WithIsolatedDeepCodeHome(() =>
        {
            VerifyUnifiedTaskFeed();
            VerifyMonitorsPublishUnifiedTasks();
            VerifyWindowsShowUnifiedTasks();
            VerifyProviderSelectionPersistence();
            VerifySettingsWritesAreAtomic();
        });
    }

    /// <summary>Feed, monitor and settings checks that never create windows; safe for the default smoke.</summary>
    private static void VerifyProviderTaskFeedWithoutWindows() => WithIsolatedDeepCodeHome(() =>
    {
        VerifyUnifiedTaskFeed();
        VerifyMonitorsPublishUnifiedTasks();
        VerifySettingsWritesAreAtomic();
    });

    /// <summary>The services created here read the default Deep Code home; isolate it so the checks
    /// do not depend on (or read) the real ~/.deepcode of a machine that uses Deep Code.</summary>
    private static void WithIsolatedDeepCodeHome(Action action)
    {
        var previousHome = Environment.GetEnvironmentVariable("DEEPCODE_HOME");
        var isolatedHome = Path.Combine(Path.GetTempPath(),
            "AIMaster-provider-deepcode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedHome);
        Environment.SetEnvironmentVariable("DEEPCODE_HOME", isolatedHome);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPCODE_HOME", previousHome);
            try { Directory.Delete(isolatedHome, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Both data sources publish into one ordered list, and every task keeps its source.</summary>
    private static void VerifyUnifiedTaskFeed()
    {
        AiTaskSnapshotFeed.Reset();
        var codexTasks = new[]
        {
            FeedTask("codex-active", "Codex active task", "inProgress", AiProviderModes.Codex, "gpt-6", "high"),
            FeedTask("codex-second", "Codex second task", "completed", AiProviderModes.Codex, "gpt-6", null)
        };
        AiTaskSnapshotFeed.Publish(AiProviderModes.Codex, codexTasks);
        Check(AiTaskSnapshotFeed.Latest is { Tasks.Count: 2 } codexView &&
              codexView.Tasks.All(task => task.Source == AiProviderModes.Codex) &&
              codexView.Tasks.Any(task => task.ModelLabel == "gpt-6 · HIGH"),
            "Codex tasks are published into the shared feed with their source and model");

        var deepTasks = new[]
        {
            FeedTask("deep-1", "Deep Code task", "waitingOnUserInput",
                AiProviderModes.DeepSeek, "deepseek-chat", "medium")
        };
        AiTaskSnapshotFeed.Publish(AiProviderModes.DeepSeek, deepTasks);
        var unified = AiTaskSnapshotFeed.Latest!;
        Check(unified.Tasks.Count == 3 &&
              unified.Tasks.Any(task => task.Id == "deep-1" && task.IsDeepSeekSource) &&
              unified.Tasks.Any(task => task.Id == "codex-active" && !task.IsDeepSeekSource) &&
              unified.Tasks[0].Id == "deep-1" && unified.Tasks[0].ModelLabel == "deepseek-chat · MEDIUM",
            "the feed merges both data sources into one ordered list labeled by source and model");

        // Rebasing one source leaves the other source's tasks untouched.
        var rebased = AiTaskSnapshotFeed.Update(AiProviderModes.Codex, current => current.Select(task =>
            task.Id == "codex-active" ? FeedTask(task.Id, "Codex active task resumed", "waitingOnApproval",
                AiProviderModes.Codex, task.ModelName, task.ReasoningEffort) : task).ToArray());
        Check(rebased.Tasks.Any(task => task.Id == "deep-1") &&
              rebased.Tasks.Any(task => task.Id == "codex-active" && task.Status == "waitingOnApproval"),
            "a local Codex rebase keeps Deep Code tasks in the unified feed");

        var providerChanges = 0;
        void CountProvider(string _) => providerChanges++;
        AiTaskSnapshotFeed.ProviderChanged += CountProvider;
        try
        {
            AiTaskSnapshotFeed.ActivateProvider(AiProviderModes.DeepSeek);
            Check(providerChanges == 1 &&
                  AiTaskSnapshotFeed.Latest!.Tasks.Count == 3,
                "switching the dashboard data source announces the new source without dropping tasks");
        }
        finally
        {
            AiTaskSnapshotFeed.ProviderChanged -= CountProvider;
            AiTaskSnapshotFeed.Reset();
        }
    }

    /// <summary>Monitors always publish both sources, whichever data source the dashboard shows.</summary>
    private static void VerifyMonitorsPublishUnifiedTasks()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-unified-monitors-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        var deepHome = Path.Combine(root, "deepcode");
        var deepProject = Path.Combine(deepHome, "projects", "demo");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(deepProject);
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, "{\"provider\":\"codex\"}");
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        File.WriteAllLines(Path.Combine(sessions, "codex-rollout.jsonl"), new[]
        {
            $"{{\"timestamp\":\"{stamp}\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"codex-thread\",\"thread_source\":\"user\"}}}}",
            $"{{\"timestamp\":\"{stamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\"}}}}"
        });
        File.WriteAllText(Path.Combine(deepProject, "sessions-index.json"),
            "{\"originalPath\":\"F:\\\\Work\\\\Demo\",\"entries\":[" +
            $"{{\"id\":\"deep-thread\",\"summary\":\"Deep Code task\",\"status\":\"completed\",\"updateTime\":\"{stamp}\",\"usage\":{{\"total_tokens\":120}}}}" +
            "]}");
        var previousDeepHome = Environment.GetEnvironmentVariable("DEEPCODE_HOME");
        Environment.SetEnvironmentVariable("DEEPCODE_HOME", deepHome);
        var service = new AiManagerService(settingsPath);
        try
        {
            AiTaskSnapshotFeed.Reset();
            service.RefreshLocalTaskSnapshot(sessions);
            service.RefreshDeepCodeTaskSnapshotAsync().GetAwaiter().GetResult();
            Check(AiTaskSnapshotFeed.Latest is { } first &&
                  first.Tasks.Any(task => task.Id == "codex-thread") &&
                  first.Tasks.Any(task => task.Id == "deep-thread" && task.IsDeepSeekSource),
                "Codex and Deep Code monitors always feed the same unified task list");

            // The dashboard may switch data source at any time; monitors keep publishing both.
            File.WriteAllText(settingsPath, "{\"provider\":\"deepseek\"}");
            service.ReloadTrackedSettings();
            service.RefreshLocalTaskSnapshot(sessions);
            service.RefreshDeepCodeTaskSnapshotAsync().GetAwaiter().GetResult();
            Check(AiTaskSnapshotFeed.Latest is { } second &&
                  second.Tasks.Any(task => task.Id == "codex-thread") &&
                  second.Tasks.Any(task => task.Id == "deep-thread"),
                "switching the dashboard data source does not hide the other source's tasks");
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("DEEPCODE_HOME", previousDeepHome);
            AiTaskSnapshotFeed.Reset();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Both windows show the unified list, per-task source/model and both quota bands.</summary>
    private static void VerifyWindowsShowUnifiedTasks()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-unified-windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var floatingSettings = Path.Combine(root, "floating-settings.json");
        var mainSettings = Path.Combine(root, "main-settings.json");
        File.WriteAllText(floatingSettings, "{\"provider\":\"deepseek\"}");
        File.WriteAllText(mainSettings, "{\"provider\":\"deepseek\"}");
        try
        {
            var attention = new ResetSignalAttention(Path.Combine(root, "attention.json"));
            var floatingService = new AiManagerService(floatingSettings);
            var floating = new AiFloatingWindow(floatingService, attention);
            var main = new AiManagerWindow(new AiManagerService(mainSettings), attention);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var displayTasks = typeof(AiFloatingWindow).GetField("_displayTasks", flags)!;

            AiTaskSnapshotFeed.Reset();
            AiTaskSnapshotFeed.Publish(AiProviderModes.Codex,
                new[] { FeedTask("codex-1", "Codex task one", "inProgress", AiProviderModes.Codex, "gpt-6", "high") });
            AiTaskSnapshotFeed.Publish(AiProviderModes.DeepSeek,
                new[] { FeedTask("deep-1", "Deep task", "waitingOnUserInput", AiProviderModes.DeepSeek, "deepseek-chat", null) });
            var shown = ((System.Collections.Generic.IReadOnlyList<AiThreadSummary>)displayTasks.GetValue(floating)!);
            Check(shown.Any(task => task.IsDeepSeekSource) && shown.Any(task => !task.IsDeepSeekSource) &&
                  shown.All(task => task.ModelName is { Length: > 0 }),
                "the floating monitor renders both data sources' tasks with their models");
            Check(((TextBlock)floating.FindName("PrimaryModelText")).Text.Length > 0 &&
                  ((TextBlock)floating.FindName("PrimarySourceText")).Text.Length > 0,
                "the floating primary row labels the task source and model");
            var dashboardTasks = ((ListBox)main.FindName("ThreadList")).Items.Cast<AiThreadSummary>().ToList();
            Check(dashboardTasks.Any(task => task.IsDeepSeekSource) && dashboardTasks.Any(task => !task.IsDeepSeekSource) &&
                  dashboardTasks.All(task => task.SourceLabel.Length > 0 && task.ModelLabel.Length > 0),
                "the dashboard task list unifies both sources and labels source and model");

            // Default quota scope shows both data sources' quota.
            var render = typeof(AiFloatingWindow).GetMethod("RenderSnapshot", flags)!;
            var now = DateTimeOffset.Now;
            var bothQuota = new AiFloatingSnapshot
            {
                Provider = AiProviderModes.DeepSeek,
                CodexFiveHourQuota = new AiLimitWindow { LimitId = "codex", WindowDurationMinutes = 300, UsedPercent = 40 },
                CodexWeeklyQuota = new AiLimitWindow { LimitId = "codex", WindowDurationMinutes = 10080, UsedPercent = 20 },
                DeepSeekBalanceText = "¥5.39",
                DeepSeekBalanceCapturedAt = now
            };
            render.Invoke(floating, [bothQuota]);
            Check(((FrameworkElement)floating.FindName("CodexQuotaRow")).Visibility == System.Windows.Visibility.Visible &&
                  ((FrameworkElement)floating.FindName("DeepSeekQuotaRow")).Visibility == System.Windows.Visibility.Visible &&
                  ((TextBlock)floating.FindName("RemainingQuotaText")).Text == "60" &&
                  ((TextBlock)floating.FindName("DeepSeekBalanceText")).Text == "¥5.39",
                "the floating monitor shows both data sources' quota by default");

            // The context menu setting can limit the quota band to the selected data source.
            var settings = floatingService.ReloadSettings();
            settings.FloatingQuotaScope = FloatingQuotaScopes.Active;
            floatingService.SaveSettings(settings);
            render.Invoke(floating, [bothQuota]);
            Check(((FrameworkElement)floating.FindName("CodexQuotaRow")).Visibility == System.Windows.Visibility.Collapsed &&
                  ((FrameworkElement)floating.FindName("DeepSeekQuotaRow")).Visibility == System.Windows.Visibility.Visible,
                "the selected-source-only scope hides the inactive data source's quota");
            render.Invoke(floating, [new AiFloatingSnapshot { Provider = AiProviderModes.Codex, FiveHourQuota = bothQuota.CodexFiveHourQuota }]);
            Check(((FrameworkElement)floating.FindName("CodexQuotaRow")).Visibility == System.Windows.Visibility.Visible &&
                  ((FrameworkElement)floating.FindName("DeepSeekQuotaRow")).Visibility == System.Windows.Visibility.Collapsed,
                "the selected-source-only scope follows the active data source");
        }
        finally
        {
            AiTaskSnapshotFeed.Reset();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Selecting a data source must survive an application restart.</summary>
    private static void VerifyProviderSelectionPersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-provider-select-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        try
        {
            var firstService = new AiManagerService(settingsPath);
            var first = new AiManagerWindow(firstService, new ResetSignalAttention(Path.Combine(root, "attention.json")));
            var deepSeekButton = (RadioButton)first.FindName("DeepSeekProviderButton");
            var codexButton = (RadioButton)first.FindName("CodexProviderButton");
            Check(deepSeekButton.IsChecked == false && codexButton.IsChecked == true,
                "a fresh dashboard starts on the saved Codex data source");
            deepSeekButton.IsChecked = true; // The user clicks the DeepSeek data source.
            Check(firstService.Settings.Provider == AiProviderModes.DeepSeek,
                "selecting DeepSeek applies immediately to the running service");

            // Simulate quitting and starting the application again.
            var reopenedService = new AiManagerService(settingsPath);
            var reopened = new AiManagerWindow(reopenedService,
                new ResetSignalAttention(Path.Combine(root, "attention2.json")));
            Check(reopened.FindName("DeepSeekProviderButton") is RadioButton { IsChecked: true } &&
                  reopened.FindName("CodexProviderButton") is RadioButton { IsChecked: false } &&
                  reopenedService.Settings.Provider == AiProviderModes.DeepSeek,
                "reopening the application restores the selected DeepSeek data source");

            ((RadioButton)reopened.FindName("CodexProviderButton")).IsChecked = true;
            var reopenedAgain = new AiManagerWindow(new AiManagerService(settingsPath),
                new ResetSignalAttention(Path.Combine(root, "attention3.json")));
            Check(reopenedAgain.FindName("CodexProviderButton") is RadioButton { IsChecked: true } &&
                  reopenedAgain.FindName("DeepSeekProviderButton") is RadioButton { IsChecked: false },
                "switching back to Codex is also saved for the next launch");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Quitting and reopening must not read a half-written settings file and lose the selection.</summary>
    private static void VerifySettingsWritesAreAtomic()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIMaster-settings-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        try
        {
            var writer = new AiManagerService(settingsPath);
            var reader = new AiManagerService(settingsPath);
            var settings = writer.ReloadSettings();
            settings.SharedUsageDeviceName = "atomic-settings-marker";
            writer.SaveSettings(settings);
            Check(new AiManagerService(settingsPath).Settings.SharedUsageDeviceName == "atomic-settings-marker",
                "settings survive a save/load round-trip with the selected data source");

            var mismatches = 0;
            long reads = 0;
            var stop = false;
            var readerTask = Task.Run(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    var loaded = reader.ReloadSettings();
                    Interlocked.Increment(ref reads);
                    // A partially written file parses as defaults, which loses the selection marker.
                    if (loaded.SharedUsageDeviceName != "atomic-settings-marker")
                        Interlocked.Increment(ref mismatches);
                }
            });
            for (var index = 0; index < 200; index++)
            {
                settings.Provider = index % 2 == 0 ? AiProviderModes.DeepSeek : AiProviderModes.Codex;
                writer.SaveSettings(settings);
            }
            Volatile.Write(ref stop, true);
            readerTask.Wait();
            Check(mismatches == 0 && reads > 20,
                "concurrent readers never observe a partially written settings file (selection and keys stay intact)");
            Check(!File.Exists(settingsPath + ".tmp"), "atomic settings writes leave no temporary file behind");
            Check(new AiManagerService(settingsPath).Settings.Provider == AiProviderModes.Codex,
                "the last saved data source is what a reopened application loads");

            var scopeService = new AiManagerService(settingsPath);
            var scoped = scopeService.ReloadSettings();
            Check(FloatingQuotaScopes.Normalize(scoped.FloatingQuotaScope) == FloatingQuotaScopes.All,
                "the floating monitor shows every data source's quota by default");
            scoped.FloatingQuotaScope = FloatingQuotaScopes.Active;
            scopeService.SaveSettings(scoped);
            Check(new AiManagerService(settingsPath).Settings.FloatingQuotaScope == FloatingQuotaScopes.Active,
                "the quota display scope is saved for the next launch");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static AiThreadSummary FeedTask(string id, string title, string status,
        string source = AiProviderModes.Codex, string? model = "gpt-6", string? effort = null) => new()
    {
        Id = id,
        Title = title,
        ConversationTitle = title,
        Status = status,
        Source = source,
        ModelName = model,
        ReasoningEffort = effort,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
