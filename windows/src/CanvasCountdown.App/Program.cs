using System.Diagnostics;
using System.Runtime.InteropServices;
using CanvasCountdown.App.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace CanvasCountdown.App;

/// <summary>
/// Starts the app, or hands this launch to the copy that is already running.
/// </summary>
/// <remarks>
/// Two copies would each schedule the same reminders and each draw a tray
/// icon, and the second would fight the first over the database. A second
/// launch — from the Start menu, a reminder, or the installer relaunching
/// after an update — therefore brings the running window forward instead,
/// carrying its arguments (which event a reminder was about, say) with it.
/// </remarks>
public static class Program
{
    /// <summary>The arguments this process was started with, minus the executable.</summary>
    public static IReadOnlyList<string> LaunchArguments { get; private set; } = [];

    [STAThread]
    private static int Main(string[] args)
    {
        LaunchArguments = args;
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Run by the uninstaller: no window, no single-instance handover.
        if (args.Contains("--prepare-for-uninstall", StringComparer.OrdinalIgnoreCase))
        {
            UninstallPreparation.Run();
            return 0;
        }

        if (!AppEnvironment.IsIsolatedLaunch)
        {
            AppIdentity.ApplyToProcess();
        }

        if (RedirectedToRunningInstance())
        {
            return 0;
        }

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    private static bool RedirectedToRunningInstance()
    {
        // An isolated launch (tests, screenshots) must never hand over to, or
        // take over from, the user's real copy.
        var key = AppEnvironment.IsIsolatedLaunch
            ? $"CanvasCountdown.Isolated.{Environment.ProcessId}"
            : "CanvasCountdown.Main";
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var primary = AppInstance.FindOrRegisterForKey(key);
        if (primary.IsCurrent)
        {
            return false;
        }

        RedirectActivation(activation, primary);
        return true;
    }

    /// <summary>
    /// The documented way to redirect from an STA Main without deadlocking:
    /// the redirect runs on a worker while this thread pumps COM.
    /// </summary>
    private static void RedirectActivation(AppActivationArguments activation, AppInstance primary)
    {
        var redirected = NativeMethods.CreateEvent(IntPtr.Zero, bManualReset: true, bInitialState: false, lpName: null);
        _ = Task.Run(() =>
        {
            primary.RedirectActivationToAsync(activation).AsTask().Wait();
            NativeMethods.SetEvent(redirected);
        });
        _ = NativeMethods.CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [redirected], out _);

        try
        {
            var process = Process.GetProcessById((int)primary.ProcessId);
            NativeMethods.SetForegroundWindow(process.MainWindowHandle);
        }
        catch (ArgumentException)
        {
            // The other copy exited in the meantime; nothing to bring forward.
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr hEvent);

        [DllImport("ole32.dll")]
        public static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint dwIndex);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
