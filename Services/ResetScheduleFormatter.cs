using System.Globalization;
using System.Text.RegularExpressions;
using AIMaster.Models;

namespace AIMaster.Services;

/// <summary>Converts explicit source deadlines; a posting timestamp is never a reset deadline.</summary>
internal static class ResetScheduleFormatter
{
    private static readonly Regex RelativeDay = new(@"\b(today|tomorrow)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Clock = new(
        @"\b(?:(?<qualifier>before|by|at)\s*)?(?<hour>1[0-2]|0?[1-9])(?::(?<minute>[0-5]\d))?\s*(?<half>am|pm)\s*(?<zone>PST|PDT|PT|UTC|GMT)\b(?![+-]\s*\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Format(CodexResetEvent item, bool compact = false, TimeZoneInfo? localZone = null)
    {
        if (item.Kind != "signal" || !TryGetTime(item.Summary, item.AnnouncedAt, out var at, out var before)) return "";
        var local = TimeZoneInfo.ConvertTime(at, localZone ?? TimeZoneInfo.Local);
        var time = local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (compact)
            return LocalizationService.L($"{time}{(before ? " 前" : "")} · 当地",
                $"{(before ? "Before " : "")}{time} · local");
        var offset = local.ToString("zzz", CultureInfo.InvariantCulture);
        return LocalizationService.L($"预告重置时间：{time}{(before ? " 前" : "")}（当地时间 UTC{offset}）",
            $"Expected reset: {(before ? "before " : "")}{time} (local UTC{offset})");
    }

    internal static bool TryGetTime(string summary, DateTimeOffset postedAt, out DateTimeOffset at, out bool before)
    {
        at = default;
        before = false;
        var days = RelativeDay.Matches(summary);
        var clocks = Clock.Matches(summary);
        // Ambiguous or undated prose stays in the source text without a guessed conversion.
        if (days.Count != 1 || clocks.Count != 1) return false;
        var clock = clocks[0];
        var hour = int.Parse(clock.Groups["hour"].Value, CultureInfo.InvariantCulture) % 12;
        if (clock.Groups["half"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase)) hour += 12;
        var minute = clock.Groups["minute"].Success ? int.Parse(clock.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        var zone = clock.Groups["zone"].Value.ToUpperInvariant();
        var offset = zone switch { "PST" => TimeSpan.FromHours(-8), "PDT" => TimeSpan.FromHours(-7), _ => TimeSpan.Zero };
        TimeZoneInfo? pacific = zone == "PT" ? TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time") : null;
        var day = pacific is null ? postedAt.ToOffset(offset).Date : TimeZoneInfo.ConvertTime(postedAt, pacific).Date;
        if (days[0].Value.Equals("tomorrow", StringComparison.OrdinalIgnoreCase)) day = day.AddDays(1);
        var local = day.AddHours(hour).AddMinutes(minute);
        if (pacific is not null)
        {
            if (pacific.IsInvalidTime(local) || pacific.IsAmbiguousTime(local)) return false;
            offset = pacific.GetUtcOffset(local);
        }
        at = new DateTimeOffset(local, offset);
        before = clock.Groups["qualifier"].Value.Equals("before", StringComparison.OrdinalIgnoreCase) ||
                 clock.Groups["qualifier"].Value.Equals("by", StringComparison.OrdinalIgnoreCase);
        return true;
    }
}
