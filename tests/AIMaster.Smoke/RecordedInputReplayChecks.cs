using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyRecordedInputReplay(string[] args)
    {
        var source = args.SkipWhile(arg => arg != "--path").Skip(1).First();
        var callId = args.SkipWhile(arg => arg != "--call-id").Skip(1).First();
        var replay = Path.GetTempFileName();
        var service = new AiManagerService();
        var sawRequest = false;
        var sawAccepted = false;
        var sawReply = false;
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(input);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                File.AppendAllText(replay, line + "\n");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("payload", out var payload)) continue;
                var type = Text(payload, "type");
                if (Text(root, "type") == "response_item" && Text(payload, "call_id") == callId)
                {
                    if (type == "function_call" && Text(payload, "name") == "request_user_input_async")
                    {
                        sawRequest = true;
                        Check(service.ReadLocalTask(replay, DateTime.UtcNow)?.Status == "waitingOnUserInput",
                            "recorded desktop async question is detected before the response");
                    }
                    else if (sawRequest && type == "function_call_output")
                    {
                        using var output = JsonDocument.Parse(Text(payload, "output")!);
                        sawAccepted = output.RootElement.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True;
                        Check(sawAccepted && service.ReadLocalTask(replay, DateTime.UtcNow)?.Status == "waitingOnUserInput",
                            "real accepted:true desktop output preserves waiting input");
                    }
                }
                if (!sawAccepted) continue;
                var text = Text(payload, "message");
                if (type == "message" && Text(payload, "role") == "user" &&
                    payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    text = string.Join("\n", content.EnumerateArray().Select(part => Text(part, "text")));
                if (text?.StartsWith("<send_user_message_question_reply>", StringComparison.Ordinal) != true ||
                    !text.Contains(callId, StringComparison.Ordinal)) continue;
                sawReply = true;
                Check(service.ReadLocalTask(replay, DateTime.UtcNow)?.Status == "inProgress",
                    "recorded desktop reply clears waiting input on the next incremental read");
                break;
            }
            Check(sawRequest && sawAccepted && sawReply, "real desktop request, acknowledgement and reply were all replayed");
        }
        finally
        {
            service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            File.Delete(replay);
        }

        static string? Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
