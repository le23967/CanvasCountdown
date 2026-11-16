using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// The name Windows knows the app by, and the link scheme reminders use to
/// open it.
/// </summary>
/// <remarks>
/// The installer registers both. The app registers them again on every
/// (non-isolated) launch, so a copy run from anywhere else — or one whose
/// registration a cleanup tool removed — still gets reminders with its own
/// name and icon, and clicking one still opens it.
/// </remarks>
internal static class AppIdentity
{
    public const string AppUserModelId = "le23967.CanvasCountdown";
    public const string LinkScheme = "canvascountdown";

    /// <summary>Ties this process's windows and toasts to the app's identity.</summary>
    public static void ApplyToProcess()
    {
        _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
    }

    public static void EnsureRegistered()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return;
        }
        var directory = Path.GetDirectoryName(executable)!;
        try
        {
            using (var identity = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}"))
            {
                identity.SetValue("DisplayName", "Canvas Countdown");
                identity.SetValue("IconUri", Path.Combine(directory, "Assets", "AppIcon-64.png"));
            }

            using var scheme = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{LinkScheme}");
            scheme.SetValue(string.Empty, "URL:Canvas Countdown");
            scheme.SetValue("URL Protocol", string.Empty);
            using (var icon = scheme.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(string.Empty, $"\"{executable}\",0");
            }
            using var command = scheme.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, $"\"{executable}\" \"%1\"");
        }
        catch (UnauthorizedAccessException)
        {
            // A locked-down profile; reminders still fire, only without the
            // app's own name and the click-to-open link.
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The event a reminder link points at, if <paramref name="argument"/> is one.</summary>
    public static Guid? EventIdFromLink(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)
            || !Uri.TryCreate(argument.Trim().Trim('"'), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, LinkScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // canvascountdown://event/<id>
        var parts = (uri.Host + uri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && string.Equals(parts[0], "event", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(parts[1], out var id)
                ? id
                : null;
    }

    public static string LinkForEvent(Guid id) => $"{LinkScheme}://event/{id:D}";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
