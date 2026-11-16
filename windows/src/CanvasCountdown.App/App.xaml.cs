using Microsoft.UI.Xaml;

namespace CanvasCountdown.App;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // A XAML resource or binding that fails to resolve otherwise only says
        // so to an attached debugger, which a friend's PC never has.
        DebugSettings.XamlResourceReferenceFailed += (_, e) => CrashLog.Note($"XAML resource reference failed: {e.Message}");
        DebugSettings.BindingFailed += (_, e) => CrashLog.Note($"Binding failed: {e.Message}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);
    }
}
