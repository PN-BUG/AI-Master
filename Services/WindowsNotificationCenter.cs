using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace AIMaster.Services;

/// <summary>Windows' inbox toast API, with unpackaged desktop identity and COM activation.</summary>
internal sealed class WindowsNotificationCenter : INotificationCenterBackend
{
    private readonly Action<Action> _dispatch;
    private readonly Action _activate;
    public event Action<string>? Activated;

    public WindowsNotificationCenter(Action<Action> dispatch, Action activate)
    {
        _dispatch = dispatch;
        _activate = activate;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
        try { NotificationShortcut.EnsureRegistered(); }
        catch
        {
            ToastNotificationManagerCompat.OnActivated -= OnActivated;
            throw;
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat args) => _dispatch(() =>
    {
        var arguments = ToastArguments.Parse(args.Argument);
        if (arguments.TryGetValue("notification", out var key)) Activated?.Invoke(key);
        _activate();
    });

    public bool IsEnabled => ToastNotificationManagerCompat.CreateToastNotifier().Setting == NotificationSetting.Enabled;

    public IReadOnlySet<string> ReadHistory() => ToastNotificationManagerCompat.History.GetHistory()
        .Select(item => item.Group + ":" + item.Tag).ToHashSet(StringComparer.Ordinal);

    internal static ToastNotification CreateToast(CenterNotification notification)
    {
        var content = new ToastContentBuilder()
            .AddArgument("action", "open-dashboard")
            .AddArgument("notification", notification.HistoryKey)
            .AddText(notification.Title)
            .AddText(notification.Body)
            .GetToastContent();
        return new ToastNotification(content.GetXml())
        {
            Tag = notification.Tag,
            Group = notification.Group,
            ExpirationTime = notification.ExpiresAt
        };
    }

    public void Show(CenterNotification notification, Action<Exception> failed)
    {
        var toast = CreateToast(notification);
        toast.Failed += (_, args) => _dispatch(() => failed(args.ErrorCode));
        ToastNotificationManagerCompat.CreateToastNotifier().Show(toast);
    }

    public void Dispose() => ToastNotificationManagerCompat.OnActivated -= OnActivated;
}
