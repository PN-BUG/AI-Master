using System.Text.RegularExpressions;

namespace AIMaster.Services;

internal static class TaskTitleFormatter
{
    // Only remove known transport wrappers; code in a user's request remains meaningful.
    internal static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var title = text.Trim();
        const string marker = "## My request:";
        var request = title.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (request >= 0) title = title[(request + marker.Length)..].Trim();
        if (new[] { "<send_user_message_question_reply>", "<external_codex_apps_open_page>",
                "<environment_context>", "<turn_aborted>", "<recommended_plugins>", "<app-context>",
                "# AGENTS.md instructions" }
            .Any(prefix => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return string.Empty;

        title = Regex.Replace(title, @"<image\b[^>]*>[\s\S]*?</image>|</?image\b[^>]*>", "",
            RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"!\[[^\]]*\]\([^\r\n)]*\)", "");
        title = Regex.Replace(title, @"\[([^\]\r\n]+)\]\((https?://[^\s)]+)\)", match =>
            Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" ? uri.Host : match.Groups[1].Value);
        return Regex.Replace(title, @"\s+", " ").Trim();
    }

    internal static string Format(string? title, string? fallback = null)
    {
        var clean = Clean(title);
        if (clean.Length == 0) clean = Clean(fallback);
        return clean.Length == 0 ? "未命名任务" : clean;
    }
}
