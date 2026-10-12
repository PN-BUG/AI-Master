using AIMaster.Models;

namespace AIMaster.Services;

internal sealed record DailyQuotaEstimate(double TodayPercent, double? RecommendedPercent, int SampleDays);

internal static class DailyQuotaPolicy
{
    public static DailyQuotaEstimate? Estimate(AiManagerSnapshot snapshot)
    {
        var today = DateOnly.FromDateTime(snapshot.CapturedAt.LocalDateTime);
        // primary/secondary describe window slots, not account/model scope.
        var weekly = snapshot.Limits.Where(item =>
                item.LimitId.Equals("codex", StringComparison.OrdinalIgnoreCase) &&
                item.WindowDurationMinutes is >= 6 * 24 * 60 and <= 8 * 24 * 60)
            .OrderBy(item => Math.Abs(item.WindowDurationMinutes - 7 * 24 * 60)).FirstOrDefault();
        if (weekly?.ResetsAt is not { } reset || reset <= snapshot.CapturedAt ||
            !double.IsFinite(weekly.UsedPercent) || weekly.UsedPercent < 0 || weekly.UsedPercent > 100)
            return null;
        var start = reset.AddMinutes(-weekly.WindowDurationMinutes);
        if (start > snapshot.CapturedAt) return null;
        var startDate = DateOnly.FromDateTime(start.LocalDateTime);
        var history = snapshot.DailyUsage.Where(item => item.Date >= startDate && item.Date <= today)
            .GroupBy(item => item.Date).ToDictionary(group => group.Key, group => group.Sum(item => (double)item.Tokens));
        // Missing account days cannot be treated as zero consumption.
        for (var date = startDate; date <= today; date = date.AddDays(1))
            if (!history.ContainsKey(date) || history[date] < 0) return null;
        var total = history.Values.Sum();
        if (total <= 0) return weekly.UsedPercent == 0 ? new(0, null, 0) : null;
        var scale = weekly.UsedPercent / total;
        var samples = history.Where(item => item.Key < today && item.Value > 0 &&
                (item.Key > startDate || start.TimeOfDay == TimeSpan.Zero))
            .Select(item => item.Value * scale).ToArray();
        double? recommendation = samples.Length == 0 ? null
            : Math.Clamp(Math.Ceiling(samples.Average() * 1.2 * 10) / 10, 0.1, 100);
        return new DailyQuotaEstimate(history[today] * scale, recommendation, samples.Length);
    }

    public static bool IsValidLimit(double value) => double.IsFinite(value) && value is >= 0.1 and <= 100;
}

internal sealed class DailyQuotaNotificationTracker
{
    private DateOnly? _notifiedDate;
    private double _notifiedLimit;

    public bool Evaluate(double? todayPercent, DateOnly today, AiManagerSettings settings)
    {
        if (!settings.DailyQuotaWarningEnabled || !settings.QuotaNotificationsEnabled)
        {
            _notifiedDate = null;
            return false;
        }
        if (todayPercent is not { } used || !double.IsFinite(used) ||
            !DailyQuotaPolicy.IsValidLimit(settings.DailyQuotaLimitPercent) || used < settings.DailyQuotaLimitPercent)
            return false;
        if (_notifiedDate == today && _notifiedLimit == settings.DailyQuotaLimitPercent) return false;
        _notifiedDate = today;
        _notifiedLimit = settings.DailyQuotaLimitPercent;
        return true;
    }
}
