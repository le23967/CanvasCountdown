using System.Reflection;

namespace CanvasCountdown.App;

/// <summary>
/// Where the app keeps things, and whether this launch is an isolated one.
/// </summary>
/// <remarks>
/// An automated launch (the UI tests, the screenshots for the website) sets
/// <c>CANVAS_COUNTDOWN_ISOLATED_LAUNCH=1</c> and is composed from isolated
/// dependencies: an in-memory database, in-memory secrets, no network, no
/// reminders, no startup entry. It therefore cannot read the user's feed URL,
/// contact Canvas, or touch their data — the same guarantee the macOS test
/// host gives.
/// </remarks>
public static class AppEnvironment
{
    public const string IsolatedLaunchVariable = "CANVAS_COUNTDOWN_ISOLATED_LAUNCH";

    public static bool IsIsolatedLaunch { get; } =
        Environment.GetEnvironmentVariable(IsolatedLaunchVariable) == "1";

    /// <summary>The version this build was stamped with, e.g. "1.0.0".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// %LOCALAPPDATA%\CanvasCountdown — the database, preferences, labels and
    /// themes. One folder, so removing the app's data is removing one folder.
    /// An isolated launch gets a throwaway folder instead.
    /// </summary>
    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    private static string ReadVersion()
    {
        var informational = typeof(AppEnvironment).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }
        // The SDK appends "+<commit>"; people only need the number.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    private static string ResolveDataDirectory()
    {
        if (IsIsolatedLaunch)
        {
            var isolated = Path.Combine(Path.GetTempPath(), "CanvasCountdown-Isolated", Environment.ProcessId.ToString());
            Directory.CreateDirectory(isolated);
            return isolated;
        }
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(root, "CanvasCountdown");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
