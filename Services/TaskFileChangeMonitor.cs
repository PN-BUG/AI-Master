using System.Diagnostics;

namespace AIMaster.Services;

/// <summary>Refreshes local task state when rollout files change, with one queued follow-up pass.</summary>
internal sealed class TaskFileChangeMonitor : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMilliseconds(500);
    private readonly object _sync = new();
    private readonly string _root;
    private readonly Func<CancellationToken, Task> _refresh;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _queuedRefresh = new(0, 1);
    private FileSystemWatcher? _watcher;
    private Task? _worker;
    private Task? _disposeTask;
    private bool _stopping;
    private int _restartWatcher;

    public TaskFileChangeMonitor(string root, Func<CancellationToken, Task> refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(refresh);
        _root = Path.GetFullPath(root);
        _refresh = refresh;
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_worker is not null) return;
            _worker = Task.Run(RunAsync);
            QueueRefresh();
        }
    }

    private async Task RunAsync()
    {
        var cancellationToken = _lifetime.Token;
        try
        {
            EnsureWatcher();
            while (!cancellationToken.IsCancellationRequested)
            {
                var changed = await _queuedRefresh.WaitAsync(RescanInterval, cancellationToken).ConfigureAwait(false);
                if (changed) await Task.Delay(Debounce, cancellationToken).ConfigureAwait(false);
                EnsureWatcher();

                // Drain before reading, so an event arriving during the read retains the next pass.
                while (_queuedRefresh.Wait(0)) { }
                cancellationToken.ThrowIfCancellationRequested();
                try { await _refresh(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TaskFileMonitor] Local task refresh failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { RemoveWatcher(); }
    }

    internal void RequestRefresh() => QueueRefresh();

    private void QueueRefresh()
    {
        lock (_sync)
        {
            if (_stopping) return;
            try { _queuedRefresh.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private void EnsureWatcher()
    {
        if (_watcher is not null && Volatile.Read(ref _restartWatcher) == 0 && Directory.Exists(_root)) return;
        RemoveWatcher();
        Interlocked.Exchange(ref _restartWatcher, 0);
        if (!Directory.Exists(_root)) return;

        FileSystemWatcher? candidate = null;
        try
        {
            candidate = new FileSystemWatcher(_root, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024
            };
            candidate.Changed += FileChanged;
            candidate.Created += FileChanged;
            candidate.Deleted += FileChanged;
            candidate.Renamed += FileRenamed;
            candidate.Error += WatcherError;
            candidate.EnableRaisingEvents = true;
            _watcher = candidate;
            candidate = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine($"[TaskFileMonitor] File watching unavailable; using local rescans: {ex.Message}");
        }
        finally { candidate?.Dispose(); }
    }

    private void FileChanged(object sender, FileSystemEventArgs args)
    {
        TaskMonitorTrace.Record("watcher", Path.GetFileNameWithoutExtension(args.FullPath), detail: args.ChangeType.ToString());
        QueueRefresh();
    }
    private void FileRenamed(object sender, RenamedEventArgs args) => QueueRefresh();

    private void WatcherError(object sender, ErrorEventArgs args)
    {
        Debug.WriteLine($"[TaskFileMonitor] File watching interrupted; resuming with a local rescan: {args.GetException().Message}");
        Interlocked.Exchange(ref _restartWatcher, 1);
        QueueRefresh();
    }

    private void RemoveWatcher()
    {
        if (_watcher is not { } watcher) return;
        _watcher = null;
        watcher.Changed -= FileChanged;
        watcher.Created -= FileChanged;
        watcher.Deleted -= FileChanged;
        watcher.Renamed -= FileRenamed;
        watcher.Error -= WatcherError;
        watcher.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _stopping = true;
            var worker = _worker ?? Task.CompletedTask;
            // Cancellation runs outside the caller's context and lock, including synchronous UI shutdown.
            _disposeTask = Task.Run(async () =>
            {
                try
                {
                    try { _lifetime.Cancel(); }
                    catch (AggregateException ex)
                    {
                        Debug.WriteLine($"[TaskFileMonitor] Callback cancellation failed: {ex.Message}");
                    }
                    await worker.ConfigureAwait(false);
                }
                finally
                {
                    _lifetime.Dispose();
                    _queuedRefresh.Dispose();
                }
            });
            return new ValueTask(_disposeTask);
        }
    }
}
