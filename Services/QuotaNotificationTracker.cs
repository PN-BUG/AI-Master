using AIMaster.Models;

namespace AIMaster.Services;

internal sealed record QuotaNotification(AiLimitWindow Limit, bool IsPauseLimit);

internal sealed class QuotaNotificationTracker
{
    private int _notifiedLevel;

    public QuotaNotification? Evaluate(IEnumerable<AiLimitWindow> limits, AiManagerSettings settings)
    {
        if (!settings.QuotaNotificationsEnabled)
        {
            _notifiedLevel = 0;
            return null;
        }

        var highest = limits.Where(item => double.IsFinite(item.UsedPercent))
            .OrderByDescending(item => item.UsedPercent).FirstOrDefault();
        // Missing quota data must not rearm an already delivered notification.
        if (highest is null) return null;
        var level = highest.UsedPercent >= settings.PausePercent ? 2
            : highest.UsedPercent >= settings.WarningPercent ? 1 : 0;
        if (level == 0)
        {
            _notifiedLevel = 0;
            return null;
        }

        if (level <= _notifiedLevel) return null;
        _notifiedLevel = level;
        return new QuotaNotification(highest, level == 2);
    }
}
