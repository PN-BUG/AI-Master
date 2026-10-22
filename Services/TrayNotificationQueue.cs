using System.Windows.Forms;

namespace AIMaster.Services;

internal sealed record TrayNotification(string Title, string Message, ToolTipIcon Icon, int Duration,
    string? Key = null, Action? Shown = null);

/// <summary>Serializes all balloons so quota/background messages cannot replace a reset receipt.</summary>
internal sealed class TrayNotificationQueue
{
    private readonly Queue<TrayNotification> _pending = new();
    private readonly Func<TrayNotification, bool> _display;
    private readonly Func<DateTimeOffset> _clock;
    private TrayNotification? _active;
    private bool _shown;
    private DateTimeOffset _deadline;

    public TrayNotificationQueue(Func<TrayNotification, bool> display, Func<DateTimeOffset>? clock = null)
    {
        _display = display;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public bool Enqueue(TrayNotification notification)
    {
        if (notification.Key is { } key &&
            (_active?.Key == key || _pending.Any(item => item.Key == key))) return true;
        if (_active is null) return Display(notification);
        _pending.Enqueue(notification);
        return true;
    }

    public void ConfirmShown()
    {
        if (_active is null || _shown) return;
        _shown = true;
        _deadline = _clock().AddMinutes(2);
        _active.Shown?.Invoke();
    }

    public void Close()
    {
        _active = null;
        _shown = false;
        while (_pending.TryDequeue(out var next))
            if (Display(next)) break;
    }

    public void Tick()
    {
        // Suppression can produce no native events at all. Release the queue without
        // treating a timeout as delivery; reopening the app can retry that reset.
        if (_active is not null && _clock() >= _deadline) Close();
    }

    private bool Display(TrayNotification notification)
    {
        _active = notification;
        _shown = false;
        _deadline = _clock().AddMinutes(2);
        if (_display(notification)) return true;
        _active = null;
        return false;
    }
}
