using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CanvasCountdown.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        SizeToFitDisplay();
        VersionText.Text = $"Version {AppEnvironment.Version}" + (AppEnvironment.IsIsolatedLaunch ? " — isolated launch" : string.Empty);
    }

    /// <summary>
    /// Opens at a comfortable size, but never larger than the screen it opens
    /// on: plenty of student laptops are 1366×768, where a fixed 1100×760
    /// window would open with its bottom edge under the taskbar.
    /// </summary>
    private void SizeToFitDisplay()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        // Window sizes are in physical pixels; the content is not laid out
        // yet, so the display scale comes from the window itself.
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var width = Math.Min((int)(1100 * scale), (int)(area.Width * 0.94));
        var height = Math.Min((int)(760 * scale), (int)(area.Height * 0.94));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + ((area.Width - width) / 2),
            area.Y + ((area.Height - height) / 2),
            width,
            height));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
