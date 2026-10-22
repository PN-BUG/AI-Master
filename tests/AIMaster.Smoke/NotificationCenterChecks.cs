using System;
using System.Collections.Generic;
using System.Linq;
using AIMaster.Services;

internal static partial class Program
{
    private static void VerifyNotificationCenter()
    {
        var now = DateTimeOffset.Parse("2026-10-22T15:00:00+08:00");
        var backend = new TestNotificationCenter();
        var receipts = 0;
        var failures = 0;
        var service = new NotificationCenterService(backend, () => now);
        service.Failed += _ => failures++;
        backend.IsEnabled = false;
        Check(!service.SendReset("Preview", "Body", "signal:one", () => receipts++) && backend.Sent.Count == 0,
            "disabled application notifications remain unsent and retryable");
        backend.IsEnabled = true;
        Check(service.SendReset("Preview", "Body", "signal:one", () => receipts++) && receipts == 0,
            "a successful toast API call alone does not confirm Notification Center retention");
        service.SendReset("Preview", "Body", "signal:one", () => receipts++);
        Check(backend.Sent.Count == 1, "pending requests for the same reset are coalesced");
        var first = backend.Sent.Single();
        backend.History.Add(first.HistoryKey);
        service.Tick();
        service.Tick();
        Check(receipts == 1 && first.ExpiresAt == now.AddDays(1),
            "a toast retained in notification history confirms once and has a one-day expiry");
        service.Dispose();
        Check(backend.History.Contains(first.HistoryKey), "exiting the app never clears Notification Center history");
        service = new NotificationCenterService(backend, () => now);
        service.Failed += _ => failures++;
        service.SendReset("Preview", "Body", "signal:one", () => receipts++);
        Check(receipts == 2 && backend.Sent.Count == 1,
            "restart confirms an existing retained notification without displaying a duplicate");
        service.SendReset("Announced", "Body", "announcement:one", () => receipts++);
        Check(backend.Sent.Count == 2 && backend.Sent[1].Tag != first.Tag,
            "a preview and a confirmed announcement have separate stable notification identities");
        now = now.AddMinutes(3);
        service.Tick();
        Check(receipts == 2 && failures == 1, "an unretained toast times out without recording success");
        service.SendReset("Announced", "Body", "announcement:one", () => receipts++);
        backend.Fail!(new InvalidOperationException("blocked"));
        Check(receipts == 2 && failures == 2, "Windows delivery failure leaves the reset unconfirmed");
        service.SendReset("Announced", "Body", "announcement:one", () => receipts++);
        backend.Click(backend.Sent.Last().HistoryKey);
        Check(receipts == 3, "clicking a toast confirms delivery even if Windows removes it before the next poll");
        service.Dispose();

        var payload = CenterNotification.Reset("<Preview> & 中文", "Body <tag> & text", "signal:xml", now);
        var toast = WindowsNotificationCenter.CreateToast(payload);
        var xml = toast.Content.GetXml();
        Check(xml.Contains("&lt;Preview&gt;") && xml.Contains("&amp;") && toast.Tag.Length == 16 &&
              toast.Group == "resets" && xml.Contains("open-dashboard"),
            "native toast XML escapes text, groups reset history, and opens the dashboard on activation");
        Console.WriteLine("Notification Center checks passed.");
    }

    private sealed class TestNotificationCenter : INotificationCenterBackend
    {
        public event Action<string>? Activated;
        public bool IsEnabled { get; set; } = true;
        public HashSet<string> History { get; } = new(StringComparer.Ordinal);
        public List<CenterNotification> Sent { get; } = new();
        public Action<Exception>? Fail { get; private set; }
        public IReadOnlySet<string> ReadHistory() => History;
        public void Show(CenterNotification notification, Action<Exception> failed)
        {
            Sent.Add(notification);
            Fail = failed;
        }
        public void Click(string key) => Activated?.Invoke(key);
        public void Dispose() { }
    }
}
