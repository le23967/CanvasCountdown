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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        VersionText.Text = $"Version {AppEnvironment.Version}" + (AppEnvironment.IsIsolatedLaunch ? " — isolated launch" : string.Empty);
    }
}
