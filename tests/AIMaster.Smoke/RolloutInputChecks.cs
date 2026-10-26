using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyRolloutInputStates()
    {
        var path = Path.GetTempFileName();
        var service = new AiManagerService();
        try
        {
            Restart();
            Append(Request("async-1", "request_user_input_async"));
            Check(Status() == "waitingOnUserInput", "an issued async question is waiting even with a completed call item");
            Append(Output("async-1", new { accepted = true }));
            Check(Status() == "waitingOnUserInput", "async accepted output acknowledges the question without answering it");

            // Structural replay of the desktop's reply; the nested questionItemId identifies the call.
            var reply = Reply("async-1");
            Append(Record("event_msg", new { type = "user_message", message = reply }));
            Check(Status() == "inProgress", "the event user-message reply clears its exact async call");
            Append(Request("async-2", "functions.request_user_input_async"));
            Append(Record("response_item", new
            {
                type = "message", role = "user", content = new[] { new { type = "input_text", text = reply } }
            }));
            Check(Status() == "waitingOnUserInput", "a duplicate old reply does not clear a newer pending call");
            Append(Request("async-3", "request_user_input_async"));
            Append(ReplyRecord("async-2"));
            Check(Status() == "waitingOnUserInput", "answering one of multiple calls preserves another pending question");
            Append(ReplyRecord("async-3"));
            Check(Status() == "inProgress", "reply user-message records clear the final pending call");

            Append(Request("multi-question", "request_user_input_async", 2));
            Append(ReplyRecord("multi-question"));
            Check(Status() == "waitingOnUserInput", "one answer does not resolve other questions in the same call");
            Append(ReplyRecord("multi-question"));
            Check(Status() == "waitingOnUserInput", "a duplicate answer does not resolve another question index");
            Append(ReplyRecord("multi-question", 1));
            Check(Status() == "inProgress", "the final question index resolves the multi-question call");

            Append(Record("response_item", new
            {
                type = "custom_tool_call", name = "exec", call_id = "code-only",
                input = "text('request_user_input_async'); // tools.request_user_input_async({questions: []})"
            }));
            Append(Record("response_item", new
            {
                type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "request_user_input_async" } }
            }));
            Append(Request("unrelated", "not_request_user_input_async"));
            Check(Status() == "inProgress", "tool-code, prose and unrelated names mentioning input do not create waiting state");

            Append(Request("sync", "functions.request_user_input"));
            Check(Status() == "waitingOnUserInput", "synchronous questions wait until their matching output");
            Append(Output("unrelated", new { answers = new { } }));
            Check(Status() == "waitingOnUserInput", "unrelated outputs cannot answer a synchronous question");
            Append(Output("sync", new { answers = new { } }));
            Check(Status() == "inProgress", "synchronous question output resumes execution");

            foreach (var outcome in new object[]
                     { new { accepted = false }, new { status = "cancelled" }, new { status = "rejected" }, new { error = "failed" } })
            {
                Append(Request("closed", "request_user_input_async"));
                Append(Output("closed", outcome));
                Check(Status() == "inProgress", "rejected, cancelled and failed async outputs clear the pending call");
            }

            foreach (var boundary in new[] { "task_complete", "turn_aborted", "task_started" })
            {
                Append(Request("boundary", "request_user_input_async"));
                Append(Record("event_msg", new { type = boundary, reason = "interrupted" }));
                Check(Status() == (boundary == "task_complete" ? "completed" : boundary == "turn_aborted" ? "interrupted" : "inProgress"),
                    $"{boundary} clears local pending questions");
            }

            Append(Record("turn_context", new { turn_id = "turn-a" }));
            Append(Request("context", "request_user_input_async"));
            Append(Record("turn_context", new { turn_id = "turn-a" }));
            Check(Status() == "waitingOnUserInput", "repeated context for the same turn preserves pending questions");
            Append(Record("turn_context", new { turn_id = "turn-b" }));
            Check(Status() == "inProgress", "a different turn context clears the previous turn's questions");

            var partial = Request("partial", "request_user_input_async");
            File.AppendAllText(path, partial[..(partial.Length / 2)]);
            Check(Status() == "inProgress", "an incomplete appended JSONL request is not applied early");
            File.AppendAllText(path, partial[(partial.Length / 2)..] + "\n");
            Check(Status() == "waitingOnUserInput", "a completed appended request is detected on the next incremental read");
            File.AppendAllText(path, "{malformed}\n");
            Append(ReplyRecord("partial"));
            Check(Status() == "inProgress", "malformed records do not prevent subsequent replies being applied");

            Append(Request("truncated", "request_user_input_async"));
            Check(Status() == "waitingOnUserInput", "the cursor has pending input before truncation");
            Restart();
            Check(Status() == "inProgress", "truncating a rollout resets its pending-input tracker");
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            File.Delete(path);
        }

        void Restart()
        {
            File.WriteAllText(path, string.Empty);
            service.ReadLocalTask(path, DateTime.UtcNow);
            Append(Record("session_meta", new { id = "input-thread", thread_source = "user" }));
            Append(Record("event_msg", new { type = "task_started" }));
        }

        void Append(string line) => File.AppendAllText(path, line + "\n");
        string? Status() => service.ReadLocalTask(path, DateTime.UtcNow)?.Status;
        static string Record(string type, object payload) => JsonSerializer.Serialize(new { type, payload });
        static string Request(string callId, string name, int questionCount = 1) => Record("response_item", new
        {
            type = "function_call", status = "completed", name, call_id = callId,
            arguments = JsonSerializer.Serialize(new
            {
                questions = Enumerable.Range(0, questionCount).Select(_ => new { title = "Fixture question" }).ToArray()
            })
        });
        static string Output(string callId, object output) => Record("response_item", new
        {
            type = "function_call_output", call_id = callId, output = JsonSerializer.Serialize(output)
        });
        static string Reply(string callId, int questionIndex = 0) => "<send_user_message_question_reply> " + JsonSerializer.Serialize(new[]
        {
            new { questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", callId, questionIndex }),
                question = "Fixture question", answer = "Fixture answer" }
        });
        static string ReplyRecord(string callId, int questionIndex = 0) => Record("response_item", new
        {
            type = "message", role = "user", content = new[] { new { type = "input_text", text = Reply(callId, questionIndex) } }
        });
    }
}
