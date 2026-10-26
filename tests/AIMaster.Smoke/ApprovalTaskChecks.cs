using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIMaster;
using AIMaster.Models;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyApprovalTasks()
    {
        var now = DateTimeOffset.Now;
        var ordered = AiManagerService.SortTasksByStatusAndTime([
            new AiThreadSummary { Id = "input", Status = "waitingOnUserInput", UpdatedAt = now },
            new AiThreadSummary { Id = "approval-old", Status = "waitingOnApproval", UpdatedAt = now.AddHours(-2) },
            new AiThreadSummary { Id = "running", Status = "inProgress", UpdatedAt = now.AddMinutes(1) },
            new AiThreadSummary { Id = "approval-new", Status = "waitingOnApproval", UpdatedAt = now.AddHours(-1) }
        ]).Select(item => item.Id);
        Check(ordered.SequenceEqual(["approval-new", "approval-old", "input", "running"]),
            "approval tasks precede newer input and running tasks, with newest approval first");
        foreach (var waiting in new[] { "waitingOnApproval", "waitingOnUserInput" })
        {
            Check(AiManagerService.ResolveExecutionStatus(waiting, "inProgress") == waiting &&
                  AiManagerService.ResolveExecutionStatus(waiting, "completed") == waiting,
                $"{waiting} takes precedence over coarse turn execution status");
        }

        var path = Path.GetTempFileName();
        var service = new AiManagerService();
        try
        {
            foreach (var waiting in new[] { "waitingOnApproval", "waitingOnUserInput" })
            foreach (var boundary in new[] { "task_started", "task_complete" })
            {
                File.WriteAllText(path, string.Empty);
                service.ReadLocalTask(path, DateTime.UtcNow);
                File.WriteAllText(path, $$$"""
                    {"type":"session_meta","payload":{"id":"approval-thread","thread_source":"user"}}
                    {"type":"event_msg","payload":{"type":"{{{boundary}}}"}}

                    """);
                var pending = new AiThreadSummary
                {
                    Id = "approval-thread", Title = "Review command access", Status = waiting,
                    ModelName = "test-model", RolloutPath = path, UpdatedAt = now
                };
                var local = service.ReadLocalTask(path, DateTime.UtcNow)!;
                var merge = typeof(AiManagerService).GetMethod("MergeFloatingTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var merged = ((System.Collections.Generic.IEnumerable<AiThreadSummary>)merge.Invoke(service,
                    new object[] { new[] { pending }, new[] { local } })!).Single();
                Check(merged.Status == waiting,
                    $"{waiting} survives merging a local {boundary} rollout");

                var enrich = typeof(AiManagerService).GetMethod("EnrichFloatingTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var enriched = ((Task<AiThreadSummary>)enrich.Invoke(service, new object[] { pending, CancellationToken.None })!)
                    .GetAwaiter().GetResult();
                Check(enriched.Status == waiting,
                    $"{waiting} survives enrichment from a local {boundary} rollout");
            }

            var resumed = new AiThreadSummary { Id = "approval-thread", Status = "active" };
            File.WriteAllText(path, string.Empty);
            service.ReadLocalTask(path, DateTime.UtcNow);
            File.WriteAllText(path, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"approval-thread\"}}\n");
            File.AppendAllText(path, "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}\n");
            var running = service.ReadLocalTask(path, DateTime.UtcNow)!;
            var mergedRunning = ((System.Collections.Generic.IEnumerable<AiThreadSummary>)typeof(AiManagerService)
                .GetMethod("MergeFloatingTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, new object[] { new[] { resumed }, new[] { running } })!).Single();
            Check(mergedRunning.Status == "inProgress", "approval does not stick after live state resumes");
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            File.Delete(path);
        }
    }

    private static void VerifyApprovalTaskVisuals(string[] args)
    {
        var output = args.SkipWhile(arg => arg != "--output").Skip(1).FirstOrDefault()
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "approval-tasks", "screenshots");
        Directory.CreateDirectory(output);
        var isolated = Path.Combine(output, "isolated", Guid.NewGuid().ToString("N"));
        var attention = new ResetSignalAttention(Path.Combine(isolated, "attention.json"));
        var main = new AiManagerWindow(new AiManagerService(Path.Combine(isolated, "main.json")), attention);
        var floating = new AiFloatingWindow(new AiManagerService(Path.Combine(isolated, "floating.json")), attention);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var language in new[] { LocalizationService.Chinese, LocalizationService.English })
        foreach (var theme in new[] { ThemeModes.Dark, ThemeModes.Light })
        {
            LocalizationService.SetLanguage(language);
            Check(!(bool)typeof(AiFloatingWindow).GetField("_refreshing", flags)!.GetValue(floating)!,
                "changing language before opening the floating window does not start live requests");
            ThemeService.Apply(main.Resources, theme);
            ThemeService.Apply(floating.Resources, theme);
            ((RadioButton)main.FindName("TasksPageButton")).IsChecked = true;
            foreach (var status in new[] { "waitingOnApproval", "waitingOnUserInput", "inProgress" })
            {
                var waiting = status != "inProgress";
                var tasks = new[]
                {
                    new AiThreadSummary { Id = "fa944ad7-5b2f-44ad-b43b-d113ed980001", Title = "Review workspace access", Status = status },
                    new AiThreadSummary { Id = "fa944ad7-5b2f-44ad-b43b-d113ed980002", Title = "Choose implementation approach", Status = status },
                    new AiThreadSummary { Id = "fa944ad7-5b2f-44ad-b43b-d113ed980003", Title = "Continue implementation", Status = "inProgress" }
                };
                typeof(AiManagerWindow).GetMethod("RenderSnapshot", flags)!.Invoke(main,
                    new object[] { new AiManagerSnapshot { Threads = tasks.ToList() } });
                var root = (FrameworkElement)main.Content;
                ArrangeRedesign(root, 960, 650);
                var list = (ListBox)main.FindName("ThreadList");
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var surface = (Border)container.Template.FindName("TaskSurface", container);
                var label = VisualDescendants(container).OfType<TextBlock>().Single(text => text.Name == "TaskStatusLabel");
                Check(SameBrush(surface.Background, main.FindResource(waiting ? "WarningSoft" : "InputSurface")) &&
                      label.Text == LocalizationService.FormatTaskStatus(status) &&
                      label.FontWeight == (waiting ? FontWeights.Bold : FontWeights.Normal),
                    $"dashboard {status} renders and restores tint/text in {language}/{theme}");
                Capture(root, 960, 650, $"dashboard-{language}-{theme}-{status}");

                typeof(AiFloatingWindow).GetMethod("RenderSnapshot", flags)!.Invoke(floating,
                    new object[] { new AiFloatingSnapshot { Tasks = tasks.ToList(), TaskName = tasks[0].Title,
                        TaskStatus = status, ModelName = "GPT-6", ReasoningEffort = "HIGH", RemainingPercent = 63 } });
                foreach (var scale in new[] { 1d, 1.5d })
                {
                    typeof(AiFloatingWindow).GetMethod("ApplyFontScale", flags)!.Invoke(floating, new object[] { scale });
                    root = (FrameworkElement)floating.Content;
                    ArrangeRedesign(root, 200, 154);
                    typeof(AiFloatingWindow).GetMethod("RefreshVisibleTaskRows", flags)!.Invoke(floating, new object[] { true });
                    ArrangeRedesign(root, 200, 154);
                    var shell = (Border)floating.FindName("Shell");
                    var badge = (TextBlock)floating.FindName("TaskStatusText");
                    var secondary = ((StackPanel)floating.FindName("SecondaryTasksPanel")).Children.OfType<Grid>().First();
                    Check(SameBrush(shell.Background, floating.FindResource(waiting ? "WarningSoft" : "FloatingShell")) &&
                          badge.Text == LocalizationService.FormatTaskStatus(status) &&
                          badge.FontWeight == (waiting ? FontWeights.Bold : FontWeights.SemiBold) &&
                          (!waiting || SameBrush(secondary.Background, floating.FindResource("WarningSoft"))),
                        $"floating primary and secondary {status} render/restore in {language}/{theme} at {scale:0.0} scale");
                    Check(badge.DesiredSize.Width <= badge.ActualWidth + 0.5 && badge.ActualHeight > 0,
                        "floating waiting status fits without truncation at the enlarged font size");
                    Capture(root, 200, 154, $"floating-{language}-{theme}-{status}-{scale:0.0}");
                }
            }
        }
        LocalizationService.SetLanguage(LocalizationService.English);

        static bool SameBrush(object? actual, object expected) =>
            actual is SolidColorBrush left && expected is SolidColorBrush right && left.Color == right.Color;

        void Capture(FrameworkElement root, int width, int height, string name)
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(stream);
        }
    }
}
