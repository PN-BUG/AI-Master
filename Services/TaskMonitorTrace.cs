using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace AIMaster.Services;

/// <summary>Bounded, metadata-only evidence for the local task update pipeline.</summary>
internal static class TaskMonitorTrace
{
    internal const int MaximumLogBytes = 256 * 1024;
    private const int QueueCapacity = 512;
    private const int MaximumDetailLength = 512;
    private static readonly object Sync = new();
    private static TraceSession? _active;
    private static Task _lastStop = Task.CompletedTask;

    public static void Start(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (Sync)
        {
            if (_active is not null) return;
            _active = new TraceSession(Path.GetFullPath(directory), _lastStop);
        }
    }

    // Callers pass identifiers, statuses, counts and file metadata only. Never pass task
    // names, messages, prompts, tool arguments or token content through detail.
    public static void Record(string stage, string? threadId = null, string? status = null,
        long? revision = null, string? detail = null)
    {
        if (stage is not ("watcher" or "scan" or "parsed" or "feed" or "ui")) return;
        var session = Volatile.Read(ref _active);
        if (session is null) return;
        session.Enqueue(new TraceEntry(DateTime.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
            stage, Limit(threadId, 128), Limit(status, 64), revision, Limit(detail, MaximumDetailLength)));
    }

    public static Task StopAsync()
    {
        lock (Sync)
        {
            if (_active is not { } session) return _lastStop;
            _active = null;
            session.Complete();
            return _lastStop = session.Worker;
        }
    }

    private static string? Limit(string? value, int maximum) =>
        value is not null && value.Length > maximum ? value[..maximum] : value;

    private sealed record TraceEntry(DateTime AtUtc, long Timestamp, long Frequency, string Stage,
        string? ThreadId, string? Status, long? Revision, string? Detail);

    private sealed class TraceSession
    {
        private const int MaximumSamplingKeys = 2048;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private readonly string _directory;
        private readonly string _path;
        private readonly string _previousPath;
        private readonly object _samplingSync = new();
        private readonly Dictionary<SamplingKey, Sample> _samples = new();
        private readonly Queue<SamplingKey> _sampleOrder = new();
        private readonly Channel<TraceEntry> _entries = Channel.CreateBounded<TraceEntry>(
            new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });

        public TraceSession(string directory, Task predecessor)
        {
            _directory = directory;
            _path = Path.Combine(directory, "task-monitor-trace.jsonl");
            _previousPath = Path.Combine(directory, "task-monitor-trace.previous.jsonl");
            Worker = Task.Run(() => RunAsync(predecessor));
        }

        public Task Worker { get; }
        public void Enqueue(TraceEntry entry)
        {
            if (ShouldRecord(entry)) _entries.Writer.TryWrite(entry);
        }
        public void Complete() => _entries.Writer.TryComplete();

        private bool ShouldRecord(TraceEntry entry)
        {
            if (entry.Stage == "parsed") return true;
            var key = entry.Stage switch
            {
                "watcher" => new SamplingKey(entry.Stage, entry.ThreadId, null),
                "scan" => new SamplingKey(entry.Stage, null, null),
                _ => new SamplingKey(entry.Stage, entry.ThreadId, entry.Detail)
            };
            lock (_samplingSync)
            {
                if (_samples.TryGetValue(key, out var previous))
                {
                    if (entry.Stage is "watcher" or "scan")
                    {
                        if (entry.Timestamp - previous.Timestamp < Stopwatch.Frequency) return false;
                    }
                    else if (entry.Status == previous.Status) return false;
                }
                else
                {
                    if (_samples.Count >= MaximumSamplingKeys) _samples.Remove(_sampleOrder.Dequeue());
                    _sampleOrder.Enqueue(key);
                }
                _samples[key] = new Sample(entry.Timestamp, entry.Status);
                return true;
            }
        }

        private sealed record SamplingKey(string Stage, string? ThreadId, string? Detail);
        private sealed record Sample(long Timestamp, string? Status);

        private async Task RunAsync(Task predecessor)
        {
            // A quick stop/start must not overlap writers or rotations in the same directory.
            await predecessor.ConfigureAwait(false);
            while (await _entries.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                await Task.Delay(100).ConfigureAwait(false);
                var batch = new List<TraceEntry>(QueueCapacity);
                while (batch.Count < QueueCapacity && _entries.Reader.TryRead(out var entry)) batch.Add(entry);
                try { WriteBatch(batch); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    System.Security.SecurityException or NotSupportedException)
                {
                    // Diagnostics must not interrupt status updates; a later batch retries.
                    Debug.WriteLine($"[TaskMonitorTrace] Trace write unavailable: {ex.GetType().Name}");
                }
            }
        }

        private void WriteBatch(IReadOnlyList<TraceEntry> batch)
        {
            Directory.CreateDirectory(_directory);
            FileStream? stream = null;
            try
            {
                foreach (var entry in batch)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
                    stream ??= OpenLog();
                    if (stream.Length + bytes.Length + 1 > MaximumLogBytes)
                    {
                        stream.Dispose();
                        stream = null;
                        File.Move(_path, _previousPath, overwrite: true);
                        stream = OpenLog();
                    }
                    stream.Write(bytes);
                    stream.WriteByte((byte)'\n');
                }
            }
            finally { stream?.Dispose(); }
        }

        private FileStream OpenLog() => new(_path, FileMode.Append, FileAccess.Write,
            FileShare.Read | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
    }
}
