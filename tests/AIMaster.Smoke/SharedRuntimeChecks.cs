using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifySharedRuntimeChecks()
    {
        // Resolve by reflection so the pre-implementation run reports the missing
        // runtime contract as an assertion instead of failing to compile.
        var runtimeType = typeof(AiManagerService).Assembly.GetType("AIMaster.Services.AiManagerRuntime");
        Check(runtimeType is not null, "production services have an explicitly owned shared runtime");
        var constructor = typeof(AiManagerService).GetConstructors()
            .SingleOrDefault(candidate => candidate.GetParameters().Any(parameter =>
                parameter.Name == "runtime" && parameter.ParameterType == runtimeType));
        Check(constructor is not null, "services can borrow a runtime without sharing pending settings");

        var directory = Path.Combine(Path.GetTempPath(), "AIMaster-shared-runtime-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(directory, "sessions");
        Directory.CreateDirectory(sessions);
        var runtime = (IAsyncDisposable)Activator.CreateInstance(runtimeType!, nonPublic: true)!;
        AiManagerService? first = null, second = null;
        try
        {
            first = NewSharedRuntimeService(constructor!, runtime, Path.Combine(directory, "first.json"));
            second = NewSharedRuntimeService(constructor!, runtime, Path.Combine(directory, "second.json"));
            foreach (var field in new[] { "_client", "_rolloutCursors", "_localUsageCursors", "_rolloutActivityIndex" })
                Check(ReferenceEquals(SharedRuntimeField(first, field), SharedRuntimeField(second, field)),
                    $"borrowing services reuse {field} instead of retaining duplicate backends");
            var secondWarning = second.Settings.WarningPercent;
            first.Settings.WarningPercent = 17;
            Check(!ReferenceEquals(first.Settings, second.Settings) && second.Settings.WarningPercent == secondWarning,
                "editing one service's unsaved settings does not alter another window's settings");

            var path = Path.Combine(sessions, "shared.jsonl");
            File.WriteAllText(path, MemoryMetadata("shared-runtime-thread") +
                MemoryEvent("task_started") + MemoryUsage(13) + "{\"unfinished\":\"");
            Check(first.ReadLocalTask(path, DateTime.UtcNow)?.Status == "inProgress" &&
                  first.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today)).TotalTokens == 13,
                "the first borrower populates real incremental task and usage cursors");
            var cursors = SharedRuntimeCursors(first).ToArray();
            Check(cursors.Length == 2,
                "both shared parsers cache the file containing an incomplete record");
            first.DisposeAsync().AsTask().GetAwaiter().GetResult();
            first = null;
            Check(SharedRuntimeCursors(second).SequenceEqual(cursors),
                "closing one borrowing service leaves shared pending records available");
            VerifySharedRuntimeClientUsable((CodexAppServerClient)SharedRuntimeField(second, "_client"));

            File.AppendAllText(path, "\"}\n" + MemoryEvent("task_complete") + MemoryUsage(5));
            Check(second.ReadLocalTask(path, DateTime.UtcNow)?.Status == "completed" &&
                  second.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today)).TotalTokens == 18,
                "the remaining borrower continues shared cursors and counts appends exactly once");
            second.DisposeAsync().AsTask().GetAwaiter().GetResult();
            second = null;
            var reopened = NewSharedRuntimeService(constructor!, runtime, Path.Combine(directory, "reopened.json"));
            try
            {
                Check(SharedRuntimeCursors(reopened).SequenceEqual(cursors),
                    "a runtime keeps its parsed cursors after all current borrowing windows close");
                Check(reopened.ReadLocalUsageSummary(sessions, DateOnly.FromDateTime(DateTime.Today)).TotalTokens == 18,
                    "a reopened window reuses the runtime without duplicating usage");
                var client = (CodexAppServerClient)SharedRuntimeField(reopened, "_client");
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Check(!SharedRuntimeCursors(reopened).Any(),
                    "runtime shutdown releases retained cursor caches and is idempotent");
                var disposed = false;
                try { client.ConnectAsync(new CancellationToken(canceled: true)).GetAwaiter().GetResult(); }
                catch (ObjectDisposedException) { disposed = true; }
                Check(disposed, "runtime shutdown owns disposal of the shared app-server client");
            }
            finally { reopened.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

            var isolatedFirst = new AiManagerService(Path.Combine(directory, "isolated-first.json"));
            var isolatedSecond = new AiManagerService(Path.Combine(directory, "isolated-second.json"));
            try
            {
                Check(!ReferenceEquals(SharedRuntimeField(isolatedFirst, "_client"),
                        SharedRuntimeField(isolatedSecond, "_client")) &&
                      !ReferenceEquals(SharedRuntimeField(isolatedFirst, "_rolloutCursors"),
                        SharedRuntimeField(isolatedSecond, "_rolloutCursors")),
                    "explicitly constructed test services keep independent owned runtimes");
                isolatedFirst.DisposeAsync().AsTask().GetAwaiter().GetResult();
                VerifySharedRuntimeClientUsable((CodexAppServerClient)SharedRuntimeField(isolatedSecond, "_client"));
            }
            finally
            {
                isolatedFirst.DisposeAsync().AsTask().GetAwaiter().GetResult();
                isolatedSecond.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            first?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            second?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AiManagerService NewSharedRuntimeService(ConstructorInfo constructor, IAsyncDisposable runtime, string path)
    {
        var parameters = constructor.GetParameters();
        var arguments = parameters.Select(parameter => parameter.DefaultValue).ToArray();
        arguments[Array.FindIndex(parameters, parameter => parameter.Name == "settingsPath")] = path;
        arguments[Array.FindIndex(parameters, parameter => parameter.Name == "runtime")] = runtime;
        return (AiManagerService)constructor.Invoke(arguments);
    }

    private static object SharedRuntimeField(AiManagerService service, string name) =>
        typeof(AiManagerService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static System.Collections.Generic.IEnumerable<object> SharedRuntimeCursors(AiManagerService service)
    {
        foreach (var name in new[] { "_rolloutCursors", "_localUsageCursors" })
        {
            var cursors = SharedRuntimeField(service, name);
            foreach (var cursor in (IEnumerable)cursors.GetType().GetProperty("Values")!.GetValue(cursors)!)
                yield return cursor!;
        }
    }

    private static void VerifySharedRuntimeClientUsable(CodexAppServerClient client)
    {
        // A canceled request validates client lifetime before executable resolution;
        // it cannot start a process or issue a real provider/network request.
        var canceled = false;
        try { client.ConnectAsync(new CancellationToken(canceled: true)).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "a surviving service retains a usable client after another service closes");
    }
}
