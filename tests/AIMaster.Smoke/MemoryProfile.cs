using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AIMaster.Services;

internal static partial class Program
{
    // Opt-in read-only replay: report counts and memory only, never conversation content.
    private static void ProfileLocalMemoryUsage(string[] args)
    {
        var root = args.SkipWhile(arg => arg != "--sessions-root").Skip(1).FirstOrDefault()
            ?? throw new ArgumentException("--sessions-root is required for a memory profile.");
        var temporary = Path.Combine(Path.GetTempPath(), "AIMaster-memory-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var service = new AiManagerService(Path.Combine(temporary, "settings.json"));
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            var tasks = service.DiscoverLocalTasks(root, 64, 32);
            var usage = service.ReadLocalUsageSummary(root, monday);
            var initialAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var initialElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            stopwatch.Restart();
            for (var pass = 0; pass < 5; pass++)
            {
                service.DiscoverLocalTasks(root, 64, 32);
                service.ReadLocalUsageSummary(root, monday);
            }
            var refreshAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var refreshElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            var retainedBuffers = ProfileRetainedBuffers(service, "_rolloutCursors") +
                                  ProfileRetainedBuffers(service, "_localUsageCursors");
            var liveHeap = GC.GetTotalMemory(forceFullCollection: true);
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                TaskCount = tasks.Count, UsageSessionCount = usage.SessionCount,
                InitialAllocatedBytes = initialAllocated, InitialElapsedMs = initialElapsedMs,
                FiveRefreshAllocatedBytes = refreshAllocated, FiveRefreshElapsedMs = refreshElapsedMs,
                RetainedLineBufferBytes = retainedBuffers, LiveManagedHeapBytes = liveHeap,
                WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64
            }));
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static long ProfileRetainedBuffers(AiManagerService service, string field)
    {
        var cursors = typeof(AiManagerService).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var values = (System.Collections.IEnumerable)cursors.GetType().GetProperty("Values")!.GetValue(cursors)!;
        long capacity = 0;
        foreach (var cursor in values)
            capacity += ((RolloutRecordProjector)cursor!.GetType().GetField("PendingLine")!.GetValue(cursor)!).Capacity;
        return capacity;
    }
}
