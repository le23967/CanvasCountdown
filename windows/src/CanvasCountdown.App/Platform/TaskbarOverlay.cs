using System.Runtime.InteropServices;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// The small badge in the corner of the app's taskbar button.
/// </summary>
/// <remarks>
/// <para>
/// It exists only while the window does, and at 16×16 pixels it fits two
/// digits and nothing else — which is why the tray icon, not this, is the
/// countdown's real home. It earns its place because Windows hides new tray
/// icons in the overflow until someone pins them, and the badge is visible
/// from the first launch.
/// </para>
/// <para>
/// A badge belongs to a taskbar button, not to the window. Windows takes the
/// button away whenever the window is hidden and makes a new, bare one when it
/// is shown again: each time the window is opened after being closed to the
/// notification area, the first time it is opened after a sign-in launch, and
/// after Explorer restarts. It says so with the "TaskbarButtonCreated"
/// message, and the badge is put back on every time.
/// </para>
/// </remarks>
internal sealed class TaskbarOverlay : IDisposable
{
    private static readonly uint TaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
    private static readonly UIntPtr SubclassId = new(0xCC01);

    private readonly IntPtr window;
    // Kept in a field: Windows holds only a pointer to it, and a collected
    // delegate would take the app down on the next message.
    private readonly SubclassProc subclass;
    private bool isSubclassed;
    private ITaskbarList3? taskbar;
    private IntPtr icon;
    private string? description;

    /// <summary>
    /// Made with the window, before it is first shown, so the message for its
    /// first taskbar button is not missed.
    /// </summary>
    public TaskbarOverlay(IntPtr window)
    {
        this.window = window;
        taskbar = CreateTaskbar();
        subclass = OnWindowMessage;
        isSubclassed = TaskbarButtonCreated != 0 && SetWindowSubclass(window, subclass, SubclassId, UIntPtr.Zero);
        if (!isSubclassed)
        {
            CrashLog.Note("Could not watch for new taskbar buttons; the badge may be missing after the window is reopened.");
        }
    }

    /// <summary>
    /// Shows <paramref name="newIcon"/> (taking ownership of the handle) with
    /// <paramref name="description"/> read out by screen readers; a zero handle
    /// clears the badge. While the window is hidden there is no button to put
    /// it on, and it goes on when Windows makes one.
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
        this.description = newIcon == IntPtr.Zero ? null : description;
        Apply();
        if (previous != IntPtr.Zero)
        {
            DestroyIcon(previous);
        }
    }

    public void Dispose()
    {
        if (isSubclassed)
        {
            RemoveWindowSubclass(window, subclass, SubclassId);
            isSubclassed = false;
        }
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

    private bool Apply()
    {
        if (taskbar is null)
        {
            return false;
        }
        try
        {
            taskbar.SetOverlayIcon(window, icon, description);
            return true;
        }
        catch (COMException)
        {
            // No button at the moment (the window is hidden).
            return false;
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == TaskbarButtonCreated)
        {
            // Nothing may escape into Windows' own code, which called this.
            try
            {
                OnButtonCreated();
            }
            catch (Exception exception)
            {
                CrashLog.Note($"Putting the badge on a new taskbar button failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void OnButtonCreated()
    {
        var applied = Apply();
        if (!applied && taskbar is not null)
        {
            // After Explorer restarts, the old taskbar object can be talking
            // to a taskbar that has gone; a new one talks to the new taskbar.
            Marshal.ReleaseComObject(taskbar);
            taskbar = CreateTaskbar();
            applied = Apply();
        }
        if (AppEnvironment.IsIsolatedLaunch)
        {
            CrashLog.Note(applied
                ? $"Taskbar badge put on a new taskbar button: {description ?? "none"}"
                : "A new taskbar button would not take the badge.");
        }
    }

    private static ITaskbarList3? CreateTaskbar()
    {
        try
        {
            var list = (ITaskbarList3)new TaskbarList();
            list.HrInit();
            return list;
        }
        catch (COMException)
        {
            // No taskbar (a server session, say). The tray icon still works.
            return null;
        }
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

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
