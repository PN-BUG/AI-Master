using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyDesktopApprovalStateChecks()
    {
        var state = new CodexDesktopTaskState();
        state.SetTrackedThreads(["thread"]);
        foreach (var method in new[] { "item/commandExecution/requestApproval", "item/fileChange/requestApproval", "item/permissions/requestApproval" })
        foreach (var id in new object[] { "approval-id", 42 })
        {
            state.Clear();
            Check(state.Observe(ApprovalSnapshot(1, [ApprovalRequest(id, method)])) && state.GetStatus("thread") == "waitingOnApproval",
                $"desktop {method} with {id.GetType().Name} id is a real pending approval");
            Check(state.Observe(ApprovalPatch(1, 2, new { op = "replace", path = new object[] { "requests" }, value = Array.Empty<object>() })) &&
                  state.GetStatus("thread") == "inProgress", "an answered or cancelled approval clears before tool completion");
        }

        state.Clear();
        state.Observe(ApprovalSnapshot(1, [ApprovalRequest("a"), new { id = "question", method = "item/tool/requestUserInput", @params = new { } }, ApprovalRequest("b")]));
        state.Observe(ApprovalPatch(1, 2, new { op = "remove", path = new object[] { "requests", 0 } }));
        Check(state.GetStatus("thread") == "waitingOnApproval", "removing one approval preserves a second pending request");
        state.Observe(ApprovalPatch(2, 3, new { op = "replace", path = new object[] { "requests", 1, "method" }, value = "item/tool/requestUserInput" }));
        Check(state.GetStatus("thread") == "inProgress", "ignored request slots preserve numeric patch positions without becoming approvals");
        state.Observe(ApprovalPatch(3, 4, new { op = "add", path = new object[] { "requests", 1 }, value = ApprovalRequest("new") }));
        Check(state.GetStatus("thread") == "waitingOnApproval", "indexed additions restore real approval state");
        state.Observe(ApprovalPatch(4, 5, new { op = "remove", path = new object[] { "requests", 1, "id" } }));
        Check(state.GetStatus("thread") == "inProgress", "an incomplete request cannot retain approval evidence");

        state.Clear();
        state.Observe(ApprovalSnapshot(1, [], new { type = "active", activeFlags = new[] { "waitingOnApproval" } },
            new[] { new { type = "automaticApprovalReview", status = "inProgress", text = "auto-review" } }));
        Check(state.GetStatus("thread") == "inProgress", "runtime approval flags and automatic review items alone never imply human approval");
        state.Observe(ApprovalPatch(1, 2, new { op = "replace", path = new object[] { "threadRuntimeStatus", "activeFlags" }, value = new[] { "waitingOnUserInput" } }));
        Check(state.GetStatus("thread") == "waitingOnUserInput", "runtime waiting input flags remain distinct from approvals");
        state.Observe(ApprovalPatch(2, 3, new { op = "replace", path = new object[] { "threadRuntimeStatus", "activeFlags", 0 }, value = "waitingOnApproval" }));
        Check(state.GetStatus("thread") == "inProgress", "nested flag replacement clears waiting input without inventing approval");
        foreach (var (runtime, expected) in new (object, string?)[]
                 { (new { type = "idle" }, "completed"), (new { type = "systemError" }, "systemError"), (new { type = "notLoaded" }, null), (new { type = "active", activeFlags = Array.Empty<string>() }, "inProgress") })
        {
            state.Observe(ApprovalSnapshot(10, [], runtime));
            Check(state.GetStatus("thread") == expected, $"desktop runtime status maps to {expected ?? "unknown"}");
        }

        state.Clear();
        state.Observe(ApprovalSnapshot(1, [], new { type = "active", activeFlags = new[] { "waitingOnUserInput" } }));
        state.Observe(ApprovalPatch(1, 2, new { op = "replace", path = Array.Empty<object>(), value = new { id = "thread", requests = Array.Empty<object>() } }));
        Check(state.GetStatus("thread") is null, "a root replacement clears runtime evidence omitted from the new conversation state");

        state.Clear();
        state.Observe(ApprovalSnapshot(1, []));
        var identityResnapshots = 0;
        state.SnapshotRequired += CountIdentityResnapshot;
        state.Observe(ApprovalPatch(1, 2, new { op = "replace", path = Array.Empty<object>(), value = new { id = "foreign-thread", requests = new[] { ApprovalRequest("foreign") } } }));
        Check(state.GetStatus("thread") is null && identityResnapshots == 1,
            "a root replacement for another conversation invalidates evidence and requests a fresh snapshot");
        state.SnapshotRequired -= CountIdentityResnapshot;

        void CountIdentityResnapshot(string _) => identityResnapshots++;

        state.Clear();
        state.Observe(ApprovalSnapshot(10, [ApprovalRequest("before-owner-reset")]));
        state.Observe(ApprovalSnapshot(1, []));
        Check(state.GetStatus("thread") == "inProgress", "an authoritative snapshot with a restarted revision clears pre-reset approvals");
        state.Observe(ApprovalPatch(3, 4, new { op = "replace", path = new object[] { "requests" }, value = Array.Empty<object>() }));
        state.Observe(ApprovalSnapshot(0, []));
        Check(state.GetStatus("thread") == "inProgress", "an authoritative lower-revision snapshot restores evidence after a gap");

        state.Clear();
        var resnapshots = new List<string>();
        state.SnapshotRequired += resnapshots.Add;
        state.Observe(ApprovalSnapshot(1, [ApprovalRequest("pending")]));
        Check(!state.Observe(ApprovalPatch(0, 1, new { op = "replace", path = new object[] { "requests" }, value = Array.Empty<object>() })) &&
              state.GetStatus("thread") == "waitingOnApproval", "duplicate and older patch revisions cannot clear current approvals");
        Check(state.Observe(ApprovalPatch(3, 4, new { op = "replace", path = new object[] { "requests" }, value = Array.Empty<object>() })) &&
              state.GetStatus("thread") is null && resnapshots.SequenceEqual(["thread"]), "a revision gap invalidates stale status and requests a fresh snapshot");
        state.Observe(ApprovalPatch(4, 5, new { op = "add", path = new object[] { "requests", 0 }, value = ApprovalRequest("stale") }));
        Check(state.GetStatus("thread") is null, "patches cannot restore evidence before a fresh snapshot");
        state.Observe(ApprovalSnapshot(6, []));
        Check(state.GetStatus("thread") == "inProgress", "a fresh snapshot recovers after a gap");
        state.Observe(ApprovalPatch(6, 7, new { op = "remove", path = new object[] { "requests", 99 } }));
        Check(state.GetStatus("thread") is null, "malformed indexed patches invalidate evidence safely");

        state.Clear();
        foreach (var invalid in new[]
                 { ApprovalSnapshot(1, [ApprovalRequest("foreign")], host: "remote"), ApprovalSnapshot(1, [ApprovalRequest("foreign")], thread: "untracked"),
                   ApprovalSnapshot(1, [ApprovalRequest("foreign")], version: 10) })
            Check(!state.Observe(invalid) && state.GetStatus("thread") is null, "foreign threads, hosts, and protocol versions cannot create approval evidence");
        state.Observe(ApprovalSnapshot(1, [ApprovalRequest("pending")]));
        using (var disconnected = JsonDocument.Parse("{\"type\":\"broadcast\",\"method\":\"client-status-changed\",\"version\":0,\"sourceClientId\":\"owner\",\"params\":{\"clientId\":\"owner\",\"status\":\"disconnected\"}}"))
            Check(state.Observe(disconnected.RootElement) && state.GetStatus("thread") is null, "owner disconnect immediately clears its observed task status");
        state.Observe(ApprovalSnapshot(1, [ApprovalRequest("pending")]));
        Check(state.Clear() && !state.Clear() && state.GetStatus("thread") is null, "connection loss clears evidence idempotently");
        state.Observe(ApprovalSnapshot(1, [ApprovalRequest("pending")]));
        using (var unavailable = JsonDocument.Parse("{\"type\":\"broadcast\",\"method\":\"thread-stream-owner-unavailable\",\"sourceClientId\":\"owner\",\"params\":{\"hostId\":\"local\",\"conversationId\":\"thread\"}}"))
            Check(state.Observe(unavailable.RootElement) && state.GetStatus("thread") is null,
                "read-only discovery invalidates stale approval evidence when its desktop owner disappears");
        state.SetTrackedThreads(Enumerable.Range(0, 40).Select(index => "tracked-" + index));
        Check(state.GetTrackedThreadIds().Count == 32 && state.GetStatus("thread") is null, "tracking bounds candidates and removes old task evidence");

        state.SetTrackedThreads(["thread"]);
        state.Observe(ApprovalSnapshot(1, [ApprovalRequest("small")], items: new[] { new { text = new string('x', 2_000_000) } }));
        Check(!RetainsDesktopPayload(state), "desktop task tracking retains no JSON document, transcript, or large text payload");
    }

    private static object ApprovalRequest(object id, string method = "item/commandExecution/requestApproval") =>
        new { id, method, @params = new { threadId = "thread", turnId = "turn", itemId = "item", reason = "fixture" } };

    private static JsonElement ApprovalSnapshot(long revision, object[] requests, object? runtime = null, object? items = null,
        string host = "local", string thread = "thread", int version = 11) => JsonSerializer.SerializeToElement(new
        {
            type = "broadcast", method = "thread-stream-state-changed", version, sourceClientId = "owner",
            @params = new { hostId = host, conversationId = thread, change = new { type = "snapshot", revision,
                conversationState = new { id = thread, requests, threadRuntimeStatus = runtime ?? new { type = "active", activeFlags = Array.Empty<string>() }, turns = items } } }
        });

    private static JsonElement ApprovalPatch(long previous, long revision, params object[] patches) => JsonSerializer.SerializeToElement(new
    {
        type = "broadcast", method = "thread-stream-state-changed", version = 11, sourceClientId = "owner",
        @params = new { hostId = "local", conversationId = "thread", change = new { type = "patches", baseRevision = previous, revision, patches } }
    });

    private static bool RetainsDesktopPayload(object value)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>();
        pending.Push(value);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is JsonElement or JsonDocument || current is string text && text.Length > 4096) return true;
            if (current is string || current is Delegate) continue;
            if (current is IEnumerable enumerable)
            {
                foreach (var item in enumerable) if (item is not null) pending.Push(item);
                continue;
            }
            var type = current.GetType();
            if (type.IsPrimitive || type.IsEnum) continue;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.GetValue(current) is { } child) pending.Push(child);
        }
        return false;
    }
}
