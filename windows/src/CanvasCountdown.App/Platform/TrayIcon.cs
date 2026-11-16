using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CanvasCountdown.App.Platform;

/// <summary>A line in the tray icon's right-click menu.</summary>
internal sealed record TrayMenuItem(string Label, Action Invoke, bool IsEnabled = true, bool IsSeparator = false)
{
    public static TrayMenuItem Separator { get; } = new(string.Empty, () => { }, IsSeparator: true);
}

/// <summary>
/// The notification-area icon that carries the countdown on Windows.
/// </summary>
/// <remarks>
/// WinUI 3 has no tray API, so this talks to the shell directly through
/// <c>Shell_NotifyIcon</c>, from a hidden window of its own that receives the
/// icon's clicks. The window is a real (invisible) top-level window rather
/// than a message-only one because Explorer announces a restart by
/// broadcasting "TaskbarCreated" to top-level windows; on that message the
/// icon is added again instead of silently vanishing until the app restarts.
///
/// The icon is identified by window and id, not by GUID: a GUID ties the icon
/// to one executable path and makes the shell refuse it from any other, which
/// would break the moment the app ran from a different folder.
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const string WindowClassName = "CanvasCountdown.TrayHost";

    private readonly NativeMethods.WndProc windowProcedure;
    private readonly uint taskbarCreatedMessage;
    private readonly IntPtr window;
    private IntPtr icon;
    private string tooltip = "Canvas Countdown";
    private bool isAdded;
    private bool isDisposed;

    public TrayIcon()
    {
        // Held in a field: the shell calls back through this delegate for as
        // long as the window exists, and a collected delegate is a crash.
        windowProcedure = WindowProcedure;
        taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(windowProcedure),
            hInstance = instance,
            lpszClassName = WindowClassName,
        };
        // Registering twice in one process fails harmlessly; the class is the same.
        NativeMethods.RegisterClassEx(ref windowClass);

        window = NativeMethods.CreateWindowEx(
            NativeMethods.WsExToolWindow,
            WindowClassName,
            "Canvas Countdown tray",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The tray icon's window could not be created.");
        }
    }

    /// <summary>A left click, or Enter/Space on the focused icon.</summary>
    public event EventHandler? Invoked;

    /// <summary>
    /// The clock or the time zone changed, or the PC woke from sleep: anything
    /// after which a countdown timer may have slept through midnight.
    /// </summary>
    public event EventHandler? SystemTimeChanged;

    /// <summary>Asked for when the menu is about to open, so it always reflects the current state.</summary>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuProvider { get; set; }

    /// <summary>The pixel size the shell draws tray icons at, for the current display scale.</summary>
    public int PreferredIconSize
    {
        get
        {
            var dpi = NativeMethods.GetDpiForWindow(window);
            if (dpi == 0)
            {
                dpi = 96;
            }
            var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SmCxSmIcon, dpi);
            return size > 0 ? size : 16;
        }
    }

    /// <summary>
    /// Shows <paramref name="newIcon"/> with <paramref name="newTooltip"/>. The
    /// tray icon takes ownership of the icon handle and destroys the previous one.
    /// </summary>
    public void Update(IntPtr newIcon, string newTooltip)
    {
        if (isDisposed)
        {
            NativeMethods.DestroyIcon(newIcon);
            return;
        }
        var previous = icon;
        icon = newIcon;
        // The shell truncates at 127 characters; cut cleanly instead.
        tooltip = newTooltip.Length > 127 ? newTooltip[..126] + "…" : newTooltip;
        AddOrModify();
        if (previous != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(previous);
        }
    }

    private void AddOrModify()
    {
        var data = CreateData();
        if (isAdded && NativeMethods.Shell_NotifyIcon(NativeMethods.NimModify, ref data))
        {
            return;
        }
        if (NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data))
        {
            isAdded = true;
            data.uVersion = NativeMethods.NotifyIconVersion4;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimSetVersion, ref data);
        }
    }

    private NativeMethods.NotifyIconData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        hWnd = window,
        uID = IconId,
        uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip | NativeMethods.NifShowTip,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            var notification = (uint)(lParam.ToInt64() & 0xFFFF);
            switch (notification)
            {
                case NativeMethods.WmLButtonUp:
                case NativeMethods.NinSelect:
                case NativeMethods.NinKeySelect:
                    Invoked?.Invoke(this, EventArgs.Empty);
                    break;
                case NativeMethods.WmContextMenu:
                case NativeMethods.WmRButtonUp:
                    ShowMenu(wParam);
                    break;
            }
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WmTimeChange
            || (message == NativeMethods.WmPowerBroadcast && wParam.ToInt64() == NativeMethods.PbtApmResumeAutomatic))
        {
            SystemTimeChanged?.Invoke(this, EventArgs.Empty);
        }
        if (message == taskbarCreatedMessage && taskbarCreatedMessage != 0)
        {
            // Explorer restarted and forgot every icon; put ours back.
            isAdded = false;
            AddOrModify();
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu(IntPtr anchor)
    {
        var items = MenuProvider?.Invoke();
        if (items is null || items.Count == 0)
        {
            return;
        }

        var menu = NativeMethods.CreatePopupMenu();
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.IsSeparator)
                {
                    NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, UIntPtr.Zero, null);
                    continue;
                }
                var flags = NativeMethods.MfString | (item.IsEnabled ? 0u : NativeMethods.MfGrayed);
                NativeMethods.AppendMenu(menu, flags, (UIntPtr)(uint)(index + 1), item.Label);
            }

            var x = (short)(anchor.ToInt64() & 0xFFFF);
            var y = (short)((anchor.ToInt64() >> 16) & 0xFFFF);
            if (x == 0 && y == 0)
            {
                NativeMethods.GetCursorPos(out var cursor);
                x = (short)cursor.X;
                y = (short)cursor.Y;
            }

            // Without this the menu does not close when the user clicks
            // elsewhere — a long-standing quirk of tray menus.
            NativeMethods.SetForegroundWindow(window);
            var chosen = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TpmReturnCmd | NativeMethods.TpmRightButton | NativeMethods.TpmBottomAlign,
                x,
                y,
                window,
                IntPtr.Zero);
            NativeMethods.PostMessage(window, 0, IntPtr.Zero, IntPtr.Zero);

            if (chosen > 0 && chosen <= items.Count)
            {
                var item = items[chosen - 1];
                if (item.IsEnabled)
                {
                    item.Invoke();
                }
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        if (isAdded)
        {
            var data = CreateData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
            isAdded = false;
        }
        if (icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(icon);
            icon = IntPtr.Zero;
        }
        NativeMethods.DestroyWindow(window);
    }

    private static class NativeMethods
    {
        public const uint NimAdd = 0;
        public const uint NimModify = 1;
        public const uint NimDelete = 2;
        public const uint NimSetVersion = 4;
        public const uint NotifyIconVersion4 = 4;
        public const uint NifMessage = 0x01;
        public const uint NifIcon = 0x02;
        public const uint NifTip = 0x04;
        public const uint NifShowTip = 0x80;
        public const uint WmLButtonUp = 0x0202;
        public const uint WmRButtonUp = 0x0205;
        public const uint WmContextMenu = 0x007B;
        public const uint NinSelect = 0x0400;
        public const uint NinKeySelect = 0x0401;
        public const uint MfString = 0x0000;
        public const uint MfGrayed = 0x0001;
        public const uint MfSeparator = 0x0800;
        public const uint TpmReturnCmd = 0x0100;
        public const uint TpmRightButton = 0x0002;
        public const uint TpmBottomAlign = 0x0020;
        public const uint WsExToolWindow = 0x00000080;
        public const int SmCxSmIcon = 49;
        public const uint WmTimeChange = 0x001E;
        public const uint WmPowerBroadcast = 0x0218;
        public const long PbtApmResumeAutomatic = 0x12;

        public delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NotifyIconData
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WndClassEx
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        public static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
