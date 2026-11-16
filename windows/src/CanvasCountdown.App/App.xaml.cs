using CanvasCountdown.App.Platform;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace CanvasCountdown.App;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Note($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        // A XAML resource or binding that fails to resolve otherwise only says
        // so to an attached debugger, which a friend's PC never has.
        DebugSettings.XamlResourceReferenceFailed += (_, e) => CrashLog.Note($"XAML resource reference failed: {e.Message}");
        DebugSettings.BindingFailed += (_, e) => CrashLog.Note($"Binding failed: {e.Message}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = AppServices.Start(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        window = new MainWindow();
        services.Attach(window);

        // Started at sign-in: the tray icon and the reminders are what matter,
        // and a window appearing on every boot would be in the way.
        var startsHidden = Program.LaunchArguments.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (!startsHidden)
        {
            window.Activate();
        }

        services.HandleArguments(Program.LaunchArguments, isFirstLaunch: true);
        AppInstance.GetCurrent().Activated += OnActivatedByAnotherLaunch;
    }

    /// <summary>
    /// A second launch handed over to this one: from the Start menu, a
    /// reminder being clicked, or the link in a reminder.
    /// </summary>
    private void OnActivatedByAnotherLaunch(object? sender, AppActivationArguments args)
    {
        var arguments = args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            ? CommandLine.Split(launch.Arguments)
            : [];
        AppServices.Current.Dispatcher.TryEnqueue(() =>
        {
            window?.Reveal();
            AppServices.Current.HandleArguments(arguments, isFirstLaunch: false);
        });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);
    }
}

/// <summary>Splits a command line the way Windows does, for arguments handed over from another launch.</summary>
internal static class CommandLine
{
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result;
    }
}
