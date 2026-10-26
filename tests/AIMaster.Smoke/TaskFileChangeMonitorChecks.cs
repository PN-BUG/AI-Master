using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyTaskFileChangeMonitor() =>
        VerifyTaskFileChangeMonitorAsync().GetAwaiter().GetResult();

    private static async Task VerifyTaskFileChangeMonitorAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AIMaster-task-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await VerifyTaskFileNotificationsAsync(directory);
            await VerifyTaskFileChangesDuringRefreshAsync(directory);
            await VerifyTaskFileMonitorDisposalAsync(directory);
            await VerifyMissingTaskDirectoryRecoveryAsync(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyTaskFileNotificationsAsync(string directory)
    {
        var root = Path.Combine(directory, "notifications");
        Directory.CreateDirectory(root);
        IReadOnlyDictionary<string, string> observed = new Dictionary<string, string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new TaskFileChangeMonitor(root, _ =>
        {
            Volatile.Write(ref observed, Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText));
            started.TrySetResult();
            return Task.CompletedTask;
        });
        monitor.Start();
        Check(await WaitForTaskMonitorAsync(() => started.Task.IsCompleted, 1500),
            "starting the local task monitor performs an initial refresh");

        var path = Path.Combine(root, "task.jsonl");
        await File.WriteAllTextAsync(path, "running");
        Check(await WaitForTaskMonitorAsync(() => Volatile.Read(ref observed).TryGetValue("task.jsonl", out var value) &&
                value == "running", 1500),
            "a new rollout refreshes task state immediately without waiting for the periodic fallback");
        await File.AppendAllTextAsync(path, "\nwaiting");
        Check(await WaitForTaskMonitorAsync(() => Volatile.Read(ref observed).TryGetValue("task.jsonl", out var value) &&
                value.EndsWith("waiting", StringComparison.Ordinal), 1500),
            "appending a rollout status triggers a fresh task snapshot without manual refresh");

        var nested = Path.Combine(root, "2026", "10", "25");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "nested.jsonl"), "nested waiting");
        Check(await WaitForTaskMonitorAsync(() => Volatile.Read(ref observed).ContainsKey("nested.jsonl"), 1500),
            "the watcher follows newly created dated folders and their rollout files");

        File.Move(path, Path.Combine(root, "renamed.jsonl"));
        Check(await WaitForTaskMonitorAsync(() => Volatile.Read(ref observed).ContainsKey("renamed.jsonl") &&
                !Volatile.Read(ref observed).ContainsKey("task.jsonl"), 1500),
            "renaming a rollout refreshes the discovered task files");
        File.Delete(Path.Combine(root, "renamed.jsonl"));
        Check(await WaitForTaskMonitorAsync(() => !Volatile.Read(ref observed).ContainsKey("renamed.jsonl"), 1500),
            "deleting a rollout refreshes the discovered task files");
    }

    private static async Task VerifyTaskFileChangesDuringRefreshAsync(string directory)
    {
        var root = Path.Combine(directory, "during-refresh");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "task.jsonl");
        await File.WriteAllTextAsync(path, "baseline");
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var concurrent = 0;
        var overlapped = 0;
        var observed = "";
        await using var monitor = new TaskFileChangeMonitor(root, async token =>
        {
            var active = Interlocked.Increment(ref concurrent);
            if (active > 1) Interlocked.Exchange(ref overlapped, 1);
            try
            {
                var pass = Interlocked.Increment(ref callbacks);
                Volatile.Write(ref observed, await File.ReadAllTextAsync(path, token));
                if (pass == 1) initial.TrySetResult();
                if (pass == 2)
                {
                    reading.TrySetResult();
                    await releaseRead.Task.WaitAsync(token);
                }
            }
            finally { Interlocked.Decrement(ref concurrent); }
        });
        monitor.Start();
        await initial.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await File.AppendAllTextAsync(path, "\nfirst-change");
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            for (var index = 0; index < 30; index++) await File.AppendAllTextAsync(path, "\nchange-" + index);
            await File.AppendAllTextAsync(path, "\nfinal-change");
        }
        finally { releaseRead.TrySetResult(); }
        Check(await WaitForTaskMonitorAsync(() => Volatile.Read(ref observed).EndsWith("final-change", StringComparison.Ordinal), 1500),
            "changes arriving during a task refresh always cause another pass with the newest content");
        Check(Volatile.Read(ref overlapped) == 0 && Volatile.Read(ref callbacks) < 10,
            "bursts coalesce into a bounded queue and never overlap task refresh callbacks");
    }

    private static async Task VerifyTaskFileMonitorDisposalAsync(string directory)
    {
        var root = Path.Combine(directory, "disposal");
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cancelled = false;
        await using var monitor = new TaskFileChangeMonitor(root, async token =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }
        });
        monitor.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var beforeWrite = Volatile.Read(ref calls);
        await File.WriteAllTextAsync(Path.Combine(root, "after-disposal.jsonl"), "waiting");
        await Task.Delay(200);
        await monitor.DisposeAsync();
        Check(cancelled && Volatile.Read(ref calls) == beforeWrite,
            "disposing the task monitor cancels and joins its active callback and prevents later callbacks");
    }

    private static async Task VerifyMissingTaskDirectoryRecoveryAsync(string directory)
    {
        var root = Path.Combine(directory, "not-created-yet");
        var path = Path.Combine(root, "2026", "10", "25", "recovered.jsonl");
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = "";
        await using var monitor = new TaskFileChangeMonitor(root, _ =>
        {
            Volatile.Write(ref observed, File.Exists(path) ? File.ReadAllText(path) : "");
            initial.TrySetResult();
            return Task.CompletedTask;
        });
        monitor.Start();
        await initial.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Directory.CreateDirectory(Path.Combine(root, "2026", "10", "25"));
        await File.WriteAllTextAsync(path, "waiting");
        Check(await WaitForTaskMonitorAsync(() => Volatile.Read(ref observed) == "waiting", 3500),
            "local monitoring recovers when the sessions folder is created after startup");
        await File.AppendAllTextAsync(path, "\nrunning");
        Check(await WaitForTaskMonitorAsync(() => Volatile.Read(ref observed).EndsWith("running", StringComparison.Ordinal), 1500),
            "a recovered sessions directory resumes immediate event-driven updates");
    }

    private static async Task<bool> WaitForTaskMonitorAsync(Func<bool> condition, int timeoutMs)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < timeoutMs) await Task.Delay(15);
        return condition();
    }
}
