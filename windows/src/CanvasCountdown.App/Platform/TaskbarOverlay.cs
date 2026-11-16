using System.Runtime.InteropServices;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// The small badge in the corner of the app's taskbar button.
/// </summary>
/// <remarks>
/// It exists only while the window does, and at 16×16 pixels it fits two
/// digits and nothing else — which is why the tray icon, not this, is the
/// countdown's real home. It earns its place because Windows hides new tray
/// icons in the overflow until someone pins them, and the badge is visible
/// from the first launch.
/// </remarks>
internal sealed class TaskbarOverlay : IDisposable
{
    private readonly IntPtr window;
    private ITaskbarList3? taskbar;
    private IntPtr icon;

    public TaskbarOverlay(IntPtr window)
    {
        this.window = window;
        try
        {
            taskbar = (ITaskbarList3)new TaskbarList();
            taskbar.HrInit();
        }
        catch (COMException)
        {
            // No taskbar (a server session, say). The tray icon still works.
            taskbar = null;
        }
    }

    /// <summary>
    /// Shows <paramref name="newIcon"/> (taking ownership of the handle) with
    /// <paramref name="description"/> read out by screen readers; a zero handle
    /// clears the badge.
    /// </summary>
    public void Update(IntPtr newIcon, string description)
    {
        if (taskbar is null)
        {
            if (newIcon != IntPtr.Zero)
            {
                DestroyIcon(newIcon);
            }
            return;
        }
        var previous = icon;
        icon = newIcon;
        try
        {
            taskbar.SetOverlayIcon(window, icon, newIcon == IntPtr.Zero ? null : description);
        }
        catch (COMException)
        {
            // The button may not exist yet (window still hidden); the next
            // update, after it is shown, will succeed.
        }
        if (previous != IntPtr.Zero)
        {
            DestroyIcon(previous);
        }
    }

    public void Dispose()
    {
        if (taskbar is not null)
        {
            try
            {
                taskbar.SetOverlayIcon(window, IntPtr.Zero, null);
            }
            catch (COMException)
            {
            }
            Marshal.ReleaseComObject(taskbar);
            taskbar = null;
        }
        if (icon != IntPtr.Zero)
        {
            DestroyIcon(icon);
            icon = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList
    {
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr tab, IntPtr mdi);
        void UnregisterTab(IntPtr tab);
        void SetTabOrder(IntPtr tab, IntPtr insertBefore);
        void SetTabActive(IntPtr tab, IntPtr mdi, uint flags);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }
}
