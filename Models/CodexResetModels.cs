using AIMaster.Services;

namespace AIMaster.Models;

internal sealed record CodexResetEvent(string Id, string Kind, DateTimeOffset AnnouncedAt,
    string Summary, string Url)
{
    public string Key => $"{Kind}:{Id}";
    public string KindText => Kind == "signal"
        ? LocalizationService.L("重置预告 / 信号", "Reset preview / signal")
        : LocalizationService.L("全局重置公告", "Global reset announcement");
    public string TimeText => AnnouncedAt.LocalDateTime.ToString("MM-dd HH:mm");
    public string Heading => $"{KindText} · {TimeText}";
    public string SourceActionText => LocalizationService.L("查看原文", "View source");
}

internal sealed record CodexResetSnapshot(IReadOnlyList<CodexResetEvent> Events,
    CodexResetEvent? Signal, DateTimeOffset? LastResetAt, DateTimeOffset FetchedAt,
    DateTimeOffset CheckedAt, bool IsStale, string? Error = null)
{
    public IEnumerable<CodexResetEvent> AlertEvents =>
        Signal is null ? Events : Events.Prepend(Signal);
}
