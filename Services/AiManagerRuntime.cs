using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace AIMaster.Services;

/// <summary>Owns the app-server connection and parsed rollout data for a desktop session.</summary>
internal sealed class AiManagerRuntime : IAsyncDisposable
{
    private const int MaximumCursorsPerKind = 128;
    private static readonly TimeSpan IdleCursorLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CachePruneInterval = TimeSpan.FromMinutes(1);
    private static readonly Lazy<AiManagerRuntime> Production = new(() => new AiManagerRuntime());
    private CodexDesktopIpcClient? _desktopClient;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private int _disposed;
    private long _lastPruneTimestamp;

    internal static AiManagerRuntime Shared => Production.Value;

    internal void StartDesktopMonitoring()
    {
        lock (_disposeSync)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_desktopClient is not null) return;
            _desktopClient = new CodexDesktopIpcClient();
            DesktopTasks.SnapshotRequired += _desktopClient.RequestSnapshot;
            _desktopClient.Start(DesktopTasks.GetTrackedThreadIds, ObserveDesktopState, connected =>
            {
                if (!connected && DesktopTasks.Clear()) DesktopStateChanged?.Invoke();
            });
        }
    }

    internal CodexDesktopTaskState DesktopTasks { get; } = new();
    internal event Action? DesktopStateChanged;

    internal void ObserveDesktopState(JsonElement message)
    {
        if (!IsDisposed && DesktopTasks.Observe(message)) DesktopStateChanged?.Invoke();
    }

    internal CodexAppServerClient Client { get; } = new();
    internal object CacheSync { get; } = new();
    internal ConcurrentDictionary<string, AiManagerService.RolloutCursor> RolloutCursors { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    internal ConcurrentDictionary<string, AiManagerService.LocalUsageCursor> LocalUsageCursors { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    internal ConcurrentDictionary<string, AiManagerService.LocalSummaryEntry> UsageSummaries { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    internal RolloutActivityIndex ActivityIndex { get; } = new();
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal CursorLease<AiManagerService.RolloutCursor> AcquireRollout(string path) =>
        Acquire(RolloutCursors, path, static () => new AiManagerService.RolloutCursor());

    internal CursorLease<AiManagerService.LocalUsageCursor> AcquireUsage(string path) =>
        Acquire(LocalUsageCursors, path, static () => new AiManagerService.LocalUsageCursor());

    private CursorLease<T> Acquire<T>(ConcurrentDictionary<string, T> cache, string path, Func<T> create)
        where T : AiManagerService.CachedCursor
    {
        lock (CacheSync)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var cursor = cache.GetOrAdd(path, static (_, factory) => factory(), create);
            cursor.ActiveReaders++;
            cursor.LastAccessTimestamp = Stopwatch.GetTimestamp();
            TrimOverflow(cache);
            return new CursorLease<T>(this, cache, cursor);
        }
    }

    private void Release<T>(ConcurrentDictionary<string, T> cache, T cursor)
        where T : AiManagerService.CachedCursor
    {
        lock (CacheSync)
        {
            cursor.ActiveReaders--;
            cursor.LastAccessTimestamp = Stopwatch.GetTimestamp();
            TrimOverflow(cache);
        }
    }

    internal sealed class CursorLease<T> : IDisposable where T : AiManagerService.CachedCursor
    {
        private AiManagerRuntime? _owner;
        private readonly ConcurrentDictionary<string, T> _cache;

        internal CursorLease(AiManagerRuntime owner, ConcurrentDictionary<string, T> cache, T cursor)
        {
            _owner = owner;
            _cache = cache;
            Value = cursor;
        }

        public T Value { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_cache, Value);
    }

    // Called at scan boundaries. Filesystem probes happen at most once a minute,
    // while each individual read only updates its monotonic access timestamp.
    internal void PruneCaches()
    {
        lock (CacheSync)
        {
            if (IsDisposed) return;
            var now = Stopwatch.GetTimestamp();
            if (_lastPruneTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_lastPruneTimestamp, now) < CachePruneInterval) return;
            _lastPruneTimestamp = now;
            PruneInactive(RolloutCursors, now);
            PruneInactive(LocalUsageCursors, now);
        }
    }

    private static void PruneInactive<T>(ConcurrentDictionary<string, T> cache, long now)
        where T : AiManagerService.CachedCursor
    {
        foreach (var pair in cache)
        {
            var cursor = pair.Value;
            if (cursor.ActiveReaders != 0) continue;
            if (Stopwatch.GetElapsedTime(cursor.LastAccessTimestamp, now) >= IdleCursorLifetime ||
                !File.Exists(pair.Key)) RemoveCursor(cache, pair.Key, cursor);
        }
        TrimOverflow(cache);
    }

    // CacheSync protects lease counts and removal; Sync protects the parser's
    // pooled buffer. Metadata stays readable by callers after their lease ends.
    private static void TrimOverflow<T>(ConcurrentDictionary<string, T> cache)
        where T : AiManagerService.CachedCursor
    {
        var excess = cache.Count - MaximumCursorsPerKind;
        if (excess <= 0) return;
        foreach (var pair in cache.Where(pair => pair.Value.ActiveReaders == 0)
                     .OrderBy(pair => pair.Value.LastAccessTimestamp).Take(excess).ToArray())
            RemoveCursor(cache, pair.Key, pair.Value);
    }

    private static void RemoveCursor<T>(ConcurrentDictionary<string, T> cache, string path, T cursor)
        where T : AiManagerService.CachedCursor
    {
        if (cursor.ActiveReaders != 0 || !cache.TryRemove(path, out var removed)) return;
        lock (removed.Sync) removed.PendingLine.Dispose();
    }

    // A startup failure can exit before any production service exists.
    internal static ValueTask DisposeSharedAsync() =>
        Production.IsValueCreated ? Production.Value.DisposeAsync() : ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Cursor acquisition checks this flag while holding CacheSync, so no new
        // leases can start while existing readers finish their final append.
        lock (CacheSync) Volatile.Write(ref _disposed, 1);
        try
        {
            if (_desktopClient is not null) await _desktopClient.DisposeAsync().ConfigureAwait(false);
            DesktopTasks.Clear();
            await Client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            while (true)
            {
                lock (CacheSync)
                {
                    if (!RolloutCursors.Values.Any(cursor => cursor.ActiveReaders != 0) &&
                        !LocalUsageCursors.Values.Any(cursor => cursor.ActiveReaders != 0))
                    {
                        foreach (var cursor in RolloutCursors.Values)
                            lock (cursor.Sync) cursor.PendingLine.Dispose();
                        RolloutCursors.Clear();
                        foreach (var cursor in LocalUsageCursors.Values)
                            lock (cursor.Sync) cursor.PendingLine.Dispose();
                        LocalUsageCursors.Clear();
                        UsageSummaries.Clear();
                        break;
                    }
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
        }
    }
}
