using System.Text.Json;

namespace AIMaster.Services;

// Rollouts record desktop questions as ordinary tool calls, not as waiting-status events.
// An async call's completed item and accepted output acknowledge delivery; replies resolve it.
internal sealed class RolloutInputTracker
{
    private sealed record PendingRequest(bool IsAsync, HashSet<int>? QuestionIndexes);
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private string? _turnId;

    public bool IsWaiting => _pending.Count > 0;
    public bool HasObservedRequest { get; private set; }

    public void Reset()
    {
        _pending.Clear();
        HasObservedRequest = false;
        _turnId = null;
    }

    public void Observe(string? recordType, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;
        var type = ReadString(payload, "type");
        if (recordType == "event_msg")
        {
            if (type == "task_started") Reset();
            else if (type is "task_complete" or "turn_aborted") _pending.Clear();
            else if (type == "user_message") ObserveReply(ReadString(payload, "message"));
            return;
        }
        if (recordType == "turn_context")
        {
            var turnId = ReadString(payload, "turn_id");
            if (turnId is not null)
            {
                if (_turnId is not null && _turnId != turnId)
                {
                    _pending.Clear();
                    HasObservedRequest = false;
                }
                _turnId = turnId;
            }
            return;
        }
        if (recordType != "response_item") return;

        var callId = ReadString(payload, "call_id");
        if (type is "function_call" or "custom_tool_call")
        {
            // Only a real tool name is evidence. Never inspect/evaluate exec source or prose.
            if (string.IsNullOrWhiteSpace(callId) || !TryGetRequestKind(ReadString(payload, "name"), out var isAsync)) return;
            HasObservedRequest = true;
            if (IsFailureStatus(ReadString(payload, "status"))) _pending.Remove(callId);
            else _pending[callId] = new PendingRequest(isAsync, ReadQuestionIndexes(payload));
        }
        else if (type is "function_call_output" or "custom_tool_call_output")
        {
            if (callId is not null && _pending.TryGetValue(callId, out var request) &&
                (!request.IsAsync || !AcknowledgesAsyncRequest(payload)))
                _pending.Remove(callId);
        }
        else if (type == "message" && ReadString(payload, "role") == "user")
        {
            ObserveReply(ReadString(payload, "text") ?? ReadString(payload, "message"));
            if (!payload.TryGetProperty("content", out var content)) return;
            if (content.ValueKind == JsonValueKind.String) ObserveReply(content.GetString());
            else if (content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray()) ObserveReply(ReadString(part, "text"));
        }
    }

    private void ObserveReply(string? text)
    {
        const string marker = "<send_user_message_question_reply>";
        const string closingMarker = "</send_user_message_question_reply>";
        if (text is null || !text.TrimStart().StartsWith(marker, StringComparison.Ordinal)) return;
        var json = text.TrimStart()[marker.Length..].Trim();
        if (json.EndsWith(closingMarker, StringComparison.Ordinal)) json = json[..^closingMarker.Length].TrimEnd();
        try
        {
            using var replies = JsonDocument.Parse(json);
            if (replies.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var reply in replies.RootElement.EnumerateArray())
            {
                var itemId = ReadString(reply, "questionItemId");
                if (itemId is null) continue;
                try
                {
                    using var id = JsonDocument.Parse(itemId);
                    var parts = id.RootElement;
                    if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() < 3 ||
                        parts[0].ValueKind != JsonValueKind.String || parts[1].ValueKind != JsonValueKind.String ||
                        parts[2].ValueKind != JsonValueKind.Number || !parts[2].TryGetInt32(out var index) || index < 0 ||
                        !TryGetRequestKind(parts[0].GetString(), out var isAsync)) continue;
                    var callId = parts[1].GetString();
                    if (callId is null || !_pending.TryGetValue(callId, out var request) || request.IsAsync != isAsync) continue;
                    if (request.QuestionIndexes is null ||
                        (request.QuestionIndexes.Remove(index) && request.QuestionIndexes.Count == 0))
                        _pending.Remove(callId);
                }
                catch (JsonException) { }
            }
        }
        catch (JsonException) { }
    }

    private static HashSet<int>? ReadQuestionIndexes(JsonElement payload)
    {
        if (!payload.TryGetProperty("arguments", out var arguments) &&
            !payload.TryGetProperty("input", out arguments)) return null;
        if (arguments.ValueKind == JsonValueKind.Object) return ReadIndexes(arguments);
        if (arguments.ValueKind != JsonValueKind.String) return null;
        try
        {
            using var document = JsonDocument.Parse(arguments.GetString()!);
            return ReadIndexes(document.RootElement);
        }
        catch (JsonException) { return null; }

        static HashSet<int>? ReadIndexes(JsonElement arguments)
        {
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("questions", out var questions) ||
                questions.ValueKind != JsonValueKind.Array || questions.GetArrayLength() == 0) return null;
            var result = new HashSet<int>();
            for (var index = 0; index < questions.GetArrayLength(); index++) result.Add(index);
            return result;
        }
    }

    private static bool AcknowledgesAsyncRequest(JsonElement payload)
    {
        if (!payload.TryGetProperty("output", out var output)) return false;
        if (output.ValueKind == JsonValueKind.Object) return IsAccepted(output);
        if (output.ValueKind != JsonValueKind.String) return false;
        try
        {
            using var document = JsonDocument.Parse(output.GetString()!);
            return IsAccepted(document.RootElement);
        }
        catch (JsonException) { return false; }

        static bool IsAccepted(JsonElement output) => output.ValueKind == JsonValueKind.Object &&
            output.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True &&
            !IsFailureStatus(ReadString(output, "status")) &&
            (!output.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null) &&
            !IsTrue(output, "isError") && !IsTrue(output, "cancelled") && !IsTrue(output, "canceled");
    }

    private static bool TryGetRequestKind(string? name, out bool isAsync)
    {
        isAsync = IsToolName(name, "request_user_input_async");
        return isAsync || IsToolName(name, "request_user_input");
    }

    private static bool IsToolName(string? name, string tool) => name is not null &&
        (name == tool || name.EndsWith("." + tool, StringComparison.Ordinal) ||
         name.EndsWith("__" + tool, StringComparison.Ordinal));

    private static bool IsFailureStatus(string? status) => status?.ToLowerInvariant() is
        "failed" or "error" or "rejected" or "denied" or "cancelled" or "canceled";
    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
