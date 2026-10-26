using System;
using System.Text;
using System.Text.Json;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyRolloutProjectionChecks()
    {
        // Removing the streaming skip would retain/allocate all of the opaque image bytes.
        using (var projector = new RolloutRecordProjector())
        {
            projector.Append("{\"opaque\":\""u8);
            var block = Encoding.UTF8.GetBytes(new string('x', 8192));
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 512; index++) projector.Append(block);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Console.WriteLine($"Projection partial-image metrics: allocated={allocated}; pending={projector.PendingLength}; capacity={projector.Capacity}");
            Check(projector.PendingLength < 128 * 1024 && projector.Capacity < 256 * 1024 && allocated < 128 * 1024,
                "partial multi-MiB images are skipped with bounded pending storage and allocation");
            projector.Append("\",\"timestamp\":\"2026-10-26T01:02:03Z\",\"type\":\"token_usage_record\",\"payload\":{\"usage\":{\"total_tokens\":37},\"opaque\":{\"total_tokens\":999}}}"u8);
            using var result = JsonDocument.Parse(projector.CompleteRecord());
            Check(result.RootElement.GetProperty("payload").GetProperty("usage").GetProperty("total_tokens").GetInt32() == 37 &&
                  result.RootElement.GetProperty("timestamp").GetString() == "2026-10-26T01:02:03Z" &&
                  !result.RootElement.TryGetProperty("opaque", out _),
                "metadata after an image survives and nested unknown fields cannot replace root usage");
        }

        const string metadata = "\uFEFF{\"payload\":{\"cwd\":\"C:\\\\示例🌙\",\"id\":\"projection-thread\",\"session_id\":\"session-id\",\"thread_source\":\"user\",\"model\":\"fixture-model\",\"effort\":\"high\",\"reasoning_effort\":\"medium\",\"turn_id\":\"turn-a\"},\"type\":\"session_meta\"}\r";
        foreach (var chunk in new[] { 1, 2, 3, 7, 65536 })
        {
            using var result = ProjectRollout(metadata, chunk);
            var payload = result.RootElement.GetProperty("payload");
            Check(payload.GetProperty("cwd").GetString() == "C:\\示例🌙" &&
                  payload.GetProperty("model").GetString() == "fixture-model" &&
                  payload.GetProperty("turn_id").GetString() == "turn-a",
                "BOM, CRLF, escaped strings, UTF8 and reversed property order survive arbitrary chunk boundaries");
        }

        // Bulk skipping must stop at controls/escapes, even between long plain runs.
        foreach (var ending in new[] { "\\q", "\u0001", "\\u12g4" })
        {
            using var projector = new RolloutRecordProjector();
            projector.Append("{\"discarded\":\""u8);
            var plain = Encoding.UTF8.GetBytes(new string('x', 8192));
            for (var index = 0; index < 64; index++) projector.Append(plain);
            foreach (var character in Encoding.UTF8.GetBytes(ending)) projector.Append(new[] { character });
            projector.Append("\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}"u8);
            Check(projector.CompleteRecord().IsEmpty,
                "discarded plain-string runs still reject malformed controls and split escape sequences");
        }
        using (var projector = new RolloutRecordProjector())
        {
            projector.Append("{\"discarded\":\""u8);
            var plain = Encoding.UTF8.GetBytes(new string('x', 8192));
            for (var index = 0; index < 64; index++) projector.Append(plain);
            foreach (var character in "\\\"\\\\\\uD83C\\uDF19"u8) projector.Append(new[] { character });
            projector.Append("\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}"u8);
            using var result = JsonDocument.Parse(projector.CompleteRecord());
            Check(result.RootElement.GetProperty("payload").GetProperty("type").GetString() == "task_complete",
                "bulk-discarded strings preserve following metadata after split escaped quotes and Unicode");
        }

        // Removing embedded JSON projection would allocate the giant prompt or lose exact question indexes.
        var tracker = new RolloutInputTracker();
        var huge = new string('x', 512 * 1024);
        Observe(new { type = "function_call", call_id = "two", name = "functions.request_user_input_async",
            status = "completed", arguments = JsonSerializer.Serialize(new
            {
                noise = huge, questions = new[] { new { prompt = huge }, new { prompt = "second" } }
            }) });
        Check(tracker.IsWaiting, "a projected encoded async request with huge prompts remains pending");
        Observe(new { type = "function_call_output", call_id = "two", output = JsonSerializer.Serialize(new
            { opaque = huge, accepted = true, status = "completed" }) });
        Check(tracker.IsWaiting, "accepted output after huge output noise acknowledges without resolving a question");
        Reply("two", 0, huge);
        Check(tracker.IsWaiting, "projected first reply leaves the second exact question index pending");
        Reply("two", 0, "duplicate");
        Check(tracker.IsWaiting, "duplicate projected replies cannot resolve another question index");
        Reply("two", 1, huge);
        Check(!tracker.IsWaiting, "projected reply ids survive huge answers and resolve their exact final index");
        Observe(new { type = "custom_tool_call", call_id = "exec", name = "exec",
            input = "text('request_user_input_async');" + huge });
        Check(!tracker.IsWaiting, "request names inside projected exec source do not create pending input");
        Observe(new { type = "function_call", call_id = "failed", name = "request_user_input_async",
            input = new { questions = new[] { new { question = "one" } } } });
        Observe(new { type = "function_call_output", call_id = "failed", output = new
            { accepted = true, error = new { details = huge } } });
        Check(!tracker.IsWaiting, "non-null output error containers remain failures after their contents are discarded");

        var text = "# AGENTS.md instructions\n" + huge + "\n## My request: old\n" + huge +
                   "\n## My request: 修复最后的请求 🌙\n" + huge;
        using (var result = ProjectRollout(JsonSerializer.Serialize(new { type = "response_item", payload = new
        {
            type = "message", role = "user", content = new object[]
            {
                new { type = "input_image", image_url = "data:image/png;base64," + huge },
                new { type = "input_text", text }
            }
        } }), 13))
        {
            var content = result.RootElement.GetProperty("payload").GetProperty("content");
            var projectedText = content[1].GetProperty("text").GetString()!;
            Check(TaskTitleFormatter.Clean(projectedText).StartsWith("修复最后的请求 🌙", StringComparison.Ordinal) &&
                  projectedText.Length < 70000 && !content[0].TryGetProperty("image_url", out _),
                "huge user records retain bounded text and the last request marker while dropping image data");
        }

        using (var projector = new RolloutRecordProjector())
        {
            foreach (var malformed in new[] { "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}",
                         "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"},\"junk\":\"\\q\"}",
                         "{\"payload\":{},}", "{}garbage", "{\"junk\":01,\"payload\":{}}" })
            {
                projector.Append(Encoding.UTF8.GetBytes(malformed));
                Check(projector.CompleteRecord().IsEmpty, "malformed records never apply even when selected fields are valid");
                projector.Reset();
            }
            projector.Append("{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}"u8);
            using var result = JsonDocument.Parse(projector.CompleteRecord());
            Check(result.RootElement.GetProperty("payload").GetProperty("type").GetString() == "task_complete",
                "a malformed record reset leaves the next complete record readable");
            projector.Reset();
            Check(projector.PendingLength == 0, "reset releases pending projection after truncation or a complete line");
        }

        void Observe(object payload)
        {
            using var result = ProjectRollout(JsonSerializer.Serialize(new { type = "response_item", payload }), 17);
            Check(result.RootElement.GetRawText().Length < 128 * 1024,
                "projected request and output metadata exclude giant prompts and tool output bodies");
            tracker.Observe("response_item", result.RootElement.GetProperty("payload"));
        }

        void Reply(string callId, int index, string answer)
        {
            var message = "<send_user_message_question_reply> " + JsonSerializer.Serialize(new[]
            {
                new { answer, questionItemId = JsonSerializer.Serialize(new object[]
                    { "request_user_input_async", callId, index }) }
            }) + "</send_user_message_question_reply>";
            Observe(new { type = "message", role = "user", content = new[] { new { type = "input_text", text = message } } });
        }
    }

    private static JsonDocument ProjectRollout(string record, int chunkSize)
    {
        using var projector = new RolloutRecordProjector();
        var bytes = Encoding.UTF8.GetBytes(record);
        for (var index = 0; index < bytes.Length; index += chunkSize)
            projector.Append(bytes.AsSpan(index, Math.Min(chunkSize, bytes.Length - index)));
        return JsonDocument.Parse(projector.CompleteRecord());
    }
}
