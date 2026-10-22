using System.Security.Cryptography;
using System.Text;

namespace AIMaster.Services;

internal sealed record CenterNotification(string Title, string Body, string Tag, string Group,
    DateTimeOffset ExpiresAt)
{
    public string HistoryKey => Group + ":" + Tag;

    public static CenterNotification Reset(string title, string body, string eventKey, DateTimeOffset now) =>
        new("AIMaster · " + title, body,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("registered-center:" + eventKey)))[..16],
            "resets", now.AddDays(1));
}

internal interface INotificationCenterBackend : IDisposable
{
    event Action<string>? Activated;
    bool IsEnabled { get; }
    IReadOnlySet<string> ReadHistory();
    void Show(CenterNotification notification, Action<Exception> failed);
}

/// <summary>A notification is confirmed only after it appears in Windows notification history.</summary>
internal sealed class NotificationCenterService : IDisposable
{
    private sealed record Pending(CenterNotification Notification, Action Retained, DateTimeOffset Deadline);
    private readonly INotificationCenterBackend _backend;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, Pending> _pending = new();
    public event Action<Exception>? Failed;

    public NotificationCenterService(INotificationCenterBackend backend, Func<DateTimeOffset>? clock = null)
    {
        _backend = backend;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _backend.Activated += OnActivated;
    }

    public bool SendReset(string title, string body, string eventKey, Action retained)
    {
        if (!_backend.IsEnabled) return false;
        var notification = CenterNotification.Reset(title, body, eventKey, _clock());
        if (_pending.ContainsKey(notification.HistoryKey)) return true;
        if (_backend.ReadHistory().Contains(notification.HistoryKey))
        {
            retained();
            return true;
        }
        _pending[notification.HistoryKey] = new(notification, retained, _clock().AddMinutes(2));
        try
        {
            _backend.Show(notification, error =>
            {
                if (_pending.Remove(notification.HistoryKey)) Failed?.Invoke(error);
            });
            Tick();
            return true;
        }
        catch
        {
            _pending.Remove(notification.HistoryKey);
            throw;
        }
    }

    public void Tick()
    {
        if (_pending.Count == 0) return;
        var history = _backend.ReadHistory();
        foreach (var (key, pending) in _pending.ToArray())
        {
            if (history.Contains(key))
            {
                _pending.Remove(key);
                pending.Retained();
            }
            else if (_clock() >= pending.Deadline)
            {
                _pending.Remove(key);
                Failed?.Invoke(new TimeoutException("Windows did not retain the notification in Notification Center."));
            }
        }
    }

    private void OnActivated(string historyKey)
    {
        // Clicking removes the toast from history. It is stronger delivery evidence
        // than a poll, and must not leave the same notification eligible for replay.
        if (_pending.Remove(historyKey, out var pending)) pending.Retained();
    }

    public void Dispose()
    {
        _backend.Activated -= OnActivated;
        _backend.Dispose(); // Never clear history on exit.
    }
}
