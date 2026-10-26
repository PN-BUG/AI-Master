using System.Text.Json;

namespace AIMaster.Services;

// A projection of the desktop owner's live state. Never retain the conversation JSON:
// it can contain complete transcripts, tool arguments, and sensitive question content.
internal sealed class CodexDesktopTaskState
{
    private const int MaximumThreads = 32;
    private const int MaximumRequests = 4096;
    private const int MaximumFlags = 64;
    private readonly object _gate = new();
    private readonly Dictionary<string, ThreadState> _threads = new(StringComparer.Ordinal);

    private sealed class ThreadState
    {
        public string? Owner;
        public long Revision = -1;
        public bool HasSnapshot;
        public bool SnapshotPending;
        public List<RequestSlot> Requests = [];
        public string? RuntimeType;
        public List<string?> Flags = [];
    }

    private sealed class RequestSlot
    {
        public string? Id;
        public string? Method;
    }

    public event Action<string>? SnapshotRequired;

    public void SetTrackedThreads(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            var wanted = ids.Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 512)
                .Distinct(StringComparer.Ordinal).Take(MaximumThreads).ToArray();
            var wantedSet = new HashSet<string>(wanted, StringComparer.Ordinal);
            foreach (var id in _threads.Keys.Where(id => !wantedSet.Contains(id)).ToArray()) _threads.Remove(id);
            foreach (var id in wanted) _threads.TryAdd(id, new ThreadState());
        }
    }

    public IReadOnlyList<string> GetTrackedThreadIds()
    {
        lock (_gate) return _threads.Keys.ToArray();
    }

    public string? GetStatus(string threadId)
    {
        lock (_gate) return _threads.TryGetValue(threadId, out var state) ? Status(state) : null;
    }

    public bool Clear()
    {
        lock (_gate)
        {
            var changed = _threads.Values.Any(state => Status(state) is not null);
            foreach (var state in _threads.Values) Reset(state);
            return changed;
        }
    }

    public bool Observe(JsonElement validatedEnvelope)
    {
        List<string> snapshots = [];
        bool changed;
        lock (_gate)
        {
            var before = _threads.ToDictionary(pair => pair.Key, pair => Status(pair.Value), StringComparer.Ordinal);
            ObserveCore(validatedEnvelope, snapshots);
            changed = _threads.Any(pair => before.GetValueOrDefault(pair.Key) != Status(pair.Value));
        }
        foreach (var id in snapshots)
        {
            // The subscriber may schedule transport work; never call it under the state lock.
            try { SnapshotRequired?.Invoke(id); }
            catch { /* A failed resubscription leaves evidence unavailable. */ }
        }
        return changed;
    }

    private void ObserveCore(JsonElement envelope, List<string> snapshots)
    {
        if (envelope.ValueKind != JsonValueKind.Object || Text(envelope, "type") != "broadcast" ||
            !envelope.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object) return;
        var method = Text(envelope, "method");
        var owner = Text(envelope, "sourceClientId");
        // Internal transport callback after read-only owner discovery fails or changes.
        if (method == "thread-stream-owner-unavailable")
        {
            if (Text(parameters, "hostId") == "local" && Text(parameters, "conversationId") is { } unavailableId &&
                _threads.TryGetValue(unavailableId, out var unavailable) && owner is not null && unavailable.Owner == owner)
                Reset(unavailable);
            return;
        }
        if (method == "client-status-changed")
        {
            if (!Integer(envelope, "version", out var version) || version != 0 || owner is null ||
                Text(parameters, "clientId") != owner || Text(parameters, "status") != "disconnected") return;
            foreach (var state in _threads.Values.Where(state => state.Owner == owner)) Reset(state);
            return;
        }
        if (method != "thread-stream-state-changed" || !Integer(envelope, "version", out var stateVersion) || stateVersion != 11 ||
            Text(parameters, "hostId") != "local" || Text(parameters, "conversationId") is not { } id ||
            !_threads.TryGetValue(id, out var current) || owner is null) return;
        if (!parameters.TryGetProperty("change", out var change) || change.ValueKind != JsonValueKind.Object ||
            !Integer(change, "revision", out var revision) || revision < 0)
        {
            Invalidate(id, current, snapshots);
            return;
        }
        var type = Text(change, "type");
        if (type == "snapshot")
        {
            // The owner's stream revision can restart after reconnect or thread removal.
            // A validated snapshot is authoritative even when its revision is lower.
            if (!change.TryGetProperty("conversationState", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                Invalidate(id, current, snapshots);
                return;
            }
            var replacement = new ThreadState { Owner = owner, Revision = revision, HasSnapshot = true };
            if (!ReadProjection(value, replacement, id)) { Invalidate(id, current, snapshots); return; }
            _threads[id] = replacement;
            return;
        }
        if (type != "patches") { Invalidate(id, current, snapshots); return; }
        if (current.Owner is not null && current.Owner != owner) return;
        if (revision <= current.Revision) return;
        if (!Integer(change, "baseRevision", out var previous) || !current.HasSnapshot || previous != current.Revision || revision <= previous ||
            !change.TryGetProperty("patches", out var patches) || patches.ValueKind != JsonValueKind.Array)
        {
            current.Revision = Math.Max(current.Revision, revision);
            Invalidate(id, current, snapshots);
            return;
        }
        foreach (var patch in patches.EnumerateArray())
            if (!ApplyPatch(current, patch, id)) { current.Revision = revision; Invalidate(id, current, snapshots); return; }
        current.Revision = revision;
    }

    private static bool ReadProjection(JsonElement value, ThreadState state, string threadId)
    {
        if (value.TryGetProperty("id", out var id) && ShortText(id) != threadId) return false;
        if (!value.TryGetProperty("requests", out var requests) || !ReadRequests(requests, out var slots)) return false;
        state.Requests = slots;
        state.RuntimeType = null;
        state.Flags.Clear();
        return !value.TryGetProperty("threadRuntimeStatus", out var runtime) || ReadRuntime(runtime, state);
    }

    private static bool ApplyPatch(ThreadState state, JsonElement patch, string threadId)
    {
        var operation = Text(patch, "op");
        if (operation is not ("add" or "remove" or "replace") || !patch.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array) return false;
        var parts = path.EnumerateArray().ToArray();
        if (parts.Length == 0)
        {
            return operation != "remove" && patch.TryGetProperty("value", out var root) && root.ValueKind == JsonValueKind.Object && ReadProjection(root, state, threadId);
        }
        if (parts[0].ValueKind != JsonValueKind.String) return false;
        var field = parts[0].GetString();
        if (field is not ("requests" or "threadRuntimeStatus")) return true;
        var value = patch.TryGetProperty("value", out var supplied) ? supplied : default;
        if (operation != "remove" && value.ValueKind == JsonValueKind.Undefined) return false;
        if (field == "requests")
        {
            if (parts.Length == 1)
            {
                if (operation == "remove") { state.Requests.Clear(); return true; }
                if (!ReadRequests(value, out var slots)) return false;
                state.Requests = slots;
                return true;
            }
            if (parts.Length == 2 && IsText(parts[1], "length")) return Resize(state.Requests, operation, value, MaximumRequests, () => new RequestSlot());
            if (!Index(parts[1], out var index)) return false;
            if (parts.Length == 2) return EditSlot(state.Requests, index, operation, value, ReadRequest, MaximumRequests);
            if (index >= state.Requests.Count || parts[2].ValueKind != JsonValueKind.String) return false;
            var property = parts[2].GetString();
            if (property is not ("id" or "method")) return true;
            if (parts.Length != 3) return false;
            if (property == "id") state.Requests[index].Id = operation == "remove" ? null : RequestId(value);
            else state.Requests[index].Method = operation == "remove" ? null : ShortText(value);
            return true;
        }
        if (parts.Length == 1)
        {
            if (operation == "remove") { state.RuntimeType = null; state.Flags.Clear(); return true; }
            return ReadRuntime(value, state);
        }
        if (parts[1].ValueKind != JsonValueKind.String) return false;
        var runtimeField = parts[1].GetString();
        if (runtimeField == "type")
        {
            if (parts.Length != 2) return false;
            state.RuntimeType = operation == "remove" ? null : ShortText(value);
            return true;
        }
        if (runtimeField != "activeFlags") return true;
        if (parts.Length == 2)
        {
            if (operation == "remove") { state.Flags.Clear(); return true; }
            if (!ReadFlags(value, out var flags)) return false;
            state.Flags = flags;
            return true;
        }
        if (parts.Length != 3) return false;
        if (IsText(parts[2], "length")) return Resize(state.Flags, operation, value, MaximumFlags, () => null);
        return Index(parts[2], out var flagIndex) && EditSlot(state.Flags, flagIndex, operation, value, ShortText, MaximumFlags);
    }

    private static bool ReadRequests(JsonElement value, out List<RequestSlot> requests)
    {
        requests = [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumRequests) return false;
        foreach (var item in value.EnumerateArray()) requests.Add(ReadRequest(item));
        return true;
    }

    private static RequestSlot ReadRequest(JsonElement value) => new()
    {
        Id = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id) ? RequestId(id) : null,
        Method = Text(value, "method")
    };

    private static bool ReadRuntime(JsonElement value, ThreadState state)
    {
        state.RuntimeType = null;
        state.Flags.Clear();
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Object) return false;
        state.RuntimeType = Text(value, "type");
        if (!value.TryGetProperty("activeFlags", out var flags)) return true;
        if (!ReadFlags(flags, out var result)) return false;
        state.Flags = result;
        return true;
    }

    private static bool ReadFlags(JsonElement value, out List<string?> flags)
    {
        flags = [];
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumFlags) return false;
        foreach (var flag in value.EnumerateArray()) flags.Add(ShortText(flag));
        return true;
    }

    private static bool EditSlot<T>(List<T> slots, int index, string operation, JsonElement value, Func<JsonElement, T> read, int maximum)
    {
        if (operation == "add")
        {
            if (index > slots.Count || slots.Count >= maximum) return false;
            slots.Insert(index, read(value));
        }
        else
        {
            if (index >= slots.Count) return false;
            if (operation == "remove") slots.RemoveAt(index);
            else slots[index] = read(value);
        }
        return true;
    }

    private static bool Resize<T>(List<T> slots, string operation, JsonElement value, int maximum, Func<T> empty)
    {
        if (operation == "remove" || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var length) || length < 0 || length > maximum) return false;
        if (length < slots.Count) slots.RemoveRange(length, slots.Count - length);
        while (slots.Count < length) slots.Add(empty());
        return true;
    }

    private static string? Status(ThreadState state)
    {
        if (!state.HasSnapshot) return null;
        if (state.Requests.Any(request => request.Id is not null && request.Method is
                "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/permissions/requestApproval")) return "waitingOnApproval";
        return state.RuntimeType switch
        {
            "active" when state.Flags.Contains("waitingOnUserInput") => "waitingOnUserInput",
            "active" => "inProgress",
            "idle" => "completed",
            "systemError" => "systemError",
            _ => null
        };
    }

    private static void Invalidate(string id, ThreadState state, List<string> snapshots)
    {
        state.HasSnapshot = false;
        state.Requests.Clear();
        state.RuntimeType = null;
        state.Flags.Clear();
        if (!state.SnapshotPending) { state.SnapshotPending = true; snapshots.Add(id); }
    }

    private static void Reset(ThreadState state)
    {
        state.Owner = null;
        state.Revision = -1;
        state.HasSnapshot = false;
        state.SnapshotPending = false;
        state.Requests.Clear();
        state.RuntimeType = null;
        state.Flags.Clear();
    }

    private static string? Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) ? ShortText(item) : null;

    private static string? ShortText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is { Length: > 0 and <= 512 } ? text : null;
    }

    private static string? RequestId(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => ShortText(value) is { } text ? "s:" + text : null,
        JsonValueKind.Number => value.GetRawText() is { Length: <= 64 } number ? "n:" + number : null,
        _ => null
    };

    private static bool Integer(JsonElement value, string property, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) &&
               item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out number);
    }

    private static bool Index(JsonElement value, out int index)
    {
        index = -1;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out index) && index >= 0;
    }

    private static bool IsText(JsonElement value, string text) => value.ValueKind == JsonValueKind.String && value.GetString() == text;
}
