using Windows.UI.Notifications;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// What the uninstaller asks of the app before its files are removed.
/// </summary>
/// <remarks>
/// Reminders are handed to Windows ahead of time, so they would otherwise keep
/// arriving — nameless, and opening nothing — after the app was gone. The
/// startup entry would point at a missing file. Both are withdrawn here. The
/// user's data is left alone on purpose: uninstalling is not the same as
/// asking for their deadlines to be deleted, and the README says where it is.
/// </remarks>
internal static class UninstallPreparation
{
    public static void Run()
    {
        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier(AppIdentity.AppUserModelId);
            foreach (var scheduled in notifier.GetScheduledToastNotifications())
            {
                notifier.RemoveFromSchedule(scheduled);
            }
            ToastNotificationManager.History.Clear(AppIdentity.AppUserModelId);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            CrashLog.Note($"Could not withdraw reminders before uninstalling: {exception.Message}");
        }

        try
        {
            new StartupRegistration().SetEnabled(false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            CrashLog.Note($"Could not remove the startup entry before uninstalling: {exception.Message}");
        }
    }
}
