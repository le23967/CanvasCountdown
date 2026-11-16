using System.Security.Cryptography;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// Reminders, as toasts handed to Windows ahead of time.
/// </summary>
/// <remarks>
/// <para>
/// Scheduled, not shown by a timer in the app: Windows delivers them whether or
/// not Canvas Countdown is running, the way macOS delivers the Mac version's.
/// Which reminder fires when, and what it says, is decided in the core
/// (<see cref="NotificationService"/>); this only hands that list to Windows,
/// replacing whatever was scheduled before so no duplicate ever arrives.
/// </para>
/// <para>
/// Clicking one opens the event it is about: each toast carries a
/// <c>canvascountdown://event/…</c> link, registered by the installer.
/// </para>
/// <para>
/// Windows never asks for permission to show toasts. They are on unless
/// someone turned them off for this app (Settings → System → Notifications),
/// and then only Settings can turn them back on; that is what "denied" means
/// here.
/// </para>
/// </remarks>
internal sealed class ToastReminders : INotificationDelivery
{
    private const string Group = "reminders";

    private static ToastNotifier Notifier => ToastNotificationManager.CreateToastNotifier(AppIdentity.AppUserModelId);

    public Task<NotificationAuthorizationStatus> AuthorizationStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(Notifier.Setting == NotificationSetting.Enabled
                ? NotificationAuthorizationStatus.Authorized
                : NotificationAuthorizationStatus.Denied);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return Task.FromResult(NotificationAuthorizationStatus.Denied);
        }
    }

    public async Task<bool> RequestAuthorizationAsync(CancellationToken cancellationToken = default)
        => await AuthorizationStatusAsync(cancellationToken) == NotificationAuthorizationStatus.Authorized;

    public Task ReplacePendingAsync(IReadOnlyList<ReminderNotificationRequest> requests, CancellationToken cancellationToken = default)
    {
        try
        {
            var notifier = Notifier;
            foreach (var scheduled in notifier.GetScheduledToastNotifications())
            {
                notifier.RemoveFromSchedule(scheduled);
            }

            // Windows refuses a delivery time in the past, and one a moment away
            // would arrive before anyone could read why.
            var earliest = DateTimeOffset.Now.AddSeconds(5);
            foreach (var request in requests.Where(request => request.TriggerDate > earliest))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toast = new ScheduledToastNotification(Content(request.Title, request.Body, request.AssignmentId), request.TriggerDate)
                {
                    // Windows rejects a scheduled toast's id at 16 characters
                    // (0x803E0120); 12 hex digits of a SHA-256 cannot collide
                    // among the few hundred reminders this ever schedules.
                    Id = ShortKey(request.Identifier, 12),
                    Tag = ShortKey(request.Identifier, 32),
                    Group = Group,
                };
                notifier.AddToSchedule(toast);
            }
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            CrashLog.Note($"Windows did not accept the reminders: 0x{exception.HResult:X8} {exception.Message}");
            throw new InvalidOperationException(
                "Windows did not accept the reminders, so none are scheduled. Check that notifications are on for Canvas Countdown in Windows Settings → System → Notifications.",
                exception);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAllPendingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var notifier = Notifier;
            foreach (var scheduled in notifier.GetScheduledToastNotifications())
            {
                notifier.RemoveFromSchedule(scheduled);
            }
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            // Nothing can be scheduled where nothing can be read back, so
            // there is nothing to take away either.
            CrashLog.Note($"Could not clear scheduled reminders: 0x{exception.HResult:X8} {exception.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>A notice shown now, not a reminder: used once, the first time the window is closed.</summary>
    public void ShowNow(string title, string body)
    {
        try
        {
            Notifier.Show(new ToastNotification(Content(title, body, eventId: null)));
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            CrashLog.Note($"Could not show a notice: {exception.Message}");
        }
    }

    /// <summary>The scheduled reminders, for the UI tests and diagnostics.</summary>
    public int ScheduledCount
    {
        get
        {
            try
            {
                return Notifier.GetScheduledToastNotifications().Count;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return 0;
            }
        }
    }

    /// <summary>Built element by element, so a title with an ampersand or a bracket cannot break the toast.</summary>
    private static XmlDocument Content(string title, string body, Guid? eventId)
    {
        var document = new XmlDocument();
        var toast = document.CreateElement("toast");
        toast.SetAttribute("activationType", "protocol");
        toast.SetAttribute("launch", eventId is { } id ? AppIdentity.LinkForEvent(id) : $"{AppIdentity.LinkScheme}://open");
        document.AppendChild(toast);

        var visual = document.CreateElement("visual");
        var binding = document.CreateElement("binding");
        binding.SetAttribute("template", "ToastGeneric");
        foreach (var line in new[] { title, body })
        {
            var text = document.CreateElement("text");
            text.InnerText = line;
            binding.AppendChild(text);
        }
        visual.AppendChild(binding);
        toast.AppendChild(visual);
        return document;
    }

    /// <summary>
    /// Windows limits a toast's tag to 64 characters; the core's identifiers
    /// run to 71. A hash of the identifier keeps it unique and inside the limit.
    /// </summary>
    private static string ShortKey(string identifier, int length)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier)))[..length];
}
