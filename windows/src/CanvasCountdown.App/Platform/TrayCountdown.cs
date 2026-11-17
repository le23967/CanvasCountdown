using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// The countdown on Windows: the notification-area icon redrawn with the
/// number of days left, and a badge on the taskbar button while the window is
/// open.
/// </summary>
/// <remarks>
/// <para>
/// The macOS Dock tile has room for "27" and "DAYS"; a tray icon is 16 pixels
/// at normal display scale (up to 32 at 200%), which fits the digits and
/// nothing else. The tooltip says the rest in words — how many days, and what
/// is due. The appearance settings and saved themes apply unchanged.
/// </para>
/// <para>
/// Windows 11 puts a new app's tray icon in the overflow (the ^ by the clock)
/// until the user pins it; the taskbar badge is there from the first launch so
/// the countdown is visible before that.
/// </para>
/// </remarks>
internal sealed class TrayCountdown : IDockRenderer, IDisposable
{
    private readonly DispatcherQueue dispatcher;
    private TrayIcon? tray;
    private TaskbarOverlay? overlay;
    private DockAppearance appearance;
    private int? daysRemaining;
    private string label = DockTileRenderPlan.DefaultHeaderLabel;
    private AssignmentListItem? item;
    private Action? quit;
    private Action? refresh;
    private MainWindow? window;

    public TrayCountdown(DispatcherQueue dispatcher, SettingsStore settings)
    {
        this.dispatcher = dispatcher;
        appearance = settings.DockAppearance;
    }

    /// <summary>Raised when Windows says the clock or time zone changed, or the PC woke up.</summary>
    public event EventHandler? SystemTimeChanged;

    public void Attach(MainWindow mainWindow, Action quit, Action refresh)
    {
        window = mainWindow;
        this.quit = quit;
        this.refresh = refresh;
        // Before the window is first shown, so the badge goes on its very
        // first taskbar button too.
        overlay ??= new TaskbarOverlay(mainWindow.Handle);
        try
        {
            tray = new TrayIcon();
            tray.Invoked += (_, _) => dispatcher.TryEnqueue(() => window?.Reveal());
            tray.SystemTimeChanged += (_, _) => dispatcher.TryEnqueue(() => SystemTimeChanged?.Invoke(this, EventArgs.Empty));
            tray.MenuProvider = Menu;
            Redraw();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or ExternalException)
        {
            // A session without a shell (some servers) has no notification
            // area; the window and the reminders still work.
            CrashLog.Note($"No notification area: {exception.Message}");
            tray = null;
        }
    }

    public void WindowDidLoad(MainWindow mainWindow) => Redraw();

    public void Render(int? daysRemaining, string label, AssignmentListItem? item)
    {
        this.daysRemaining = daysRemaining;
        this.label = string.IsNullOrWhiteSpace(label) ? DockTileRenderPlan.DefaultHeaderLabel : label;
        this.item = item;
        Redraw();
    }

    public void Apply(DockAppearance appearance)
    {
        this.appearance = appearance;
        Redraw();
    }

    private void Redraw()
    {
        if (tray is not null)
        {
            var size = tray.PreferredIconSize;
            tray.Update(TileRenderer.RenderIcon(appearance, daysRemaining, label, size), Tooltip());
            if (AppEnvironment.IsIsolatedLaunch)
            {
                CrashLog.Note($"Tray icon drawn at {size} px: {Tooltip()}");
            }
        }
        if (overlay is not null)
        {
            overlay.Update(
                daysRemaining is null ? IntPtr.Zero : TileRenderer.RenderIcon(appearance, daysRemaining, label, 16),
                Tooltip());
        }
    }

    /// <summary>"5 days left · Essay draft (COMP2050)" — what the 16 pixels cannot say.</summary>
    private string Tooltip()
    {
        if (daysRemaining is not { } days || item is null)
        {
            return "Canvas Countdown — nothing due";
        }
        var count = days switch
        {
            0 => "Due today",
            1 => "1 day left",
            _ => $"{days.ToString(CultureInfo.CurrentCulture)} days left",
        };
        var course = item.NormalizedCourseName is { } name ? $" ({name})" : string.Empty;
        return $"{count} · {item.Title}{course}";
    }

    private IReadOnlyList<TrayMenuItem> Menu()
    {
        var items = new List<TrayMenuItem>
        {
            new("Open Canvas Countdown", () => dispatcher.TryEnqueue(() => window?.Reveal())),
            new("Refresh Canvas Feed", () => dispatcher.TryEnqueue(() => refresh?.Invoke())),
            TrayMenuItem.Separator,
            new("Quit Canvas Countdown", () => dispatcher.TryEnqueue(() => quit?.Invoke())),
        };
        return items;
    }

    public void Dispose()
    {
        tray?.Dispose();
        tray = null;
        overlay?.Dispose();
        overlay = null;
    }
}
