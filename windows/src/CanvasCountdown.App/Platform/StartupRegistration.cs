using Microsoft.Win32;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// Launch at login, through the per-user <c>Run</c> key — the Windows
/// counterpart of the macOS login item.
/// </summary>
/// <remarks>
/// Windows lets people switch startup apps off in Task Manager → Startup apps
/// (or Settings → Apps → Startup) without touching the <c>Run</c> key; that
/// choice lives under <c>StartupApproved</c>. Reading only the <c>Run</c> key
/// would show "on" in Settings for an app Windows will not start, so both are
/// read. Switching it on from Settings is an explicit request, so it also
/// clears a "disabled" mark left there.
///
/// The registered path is refreshed on every launch while the setting is on,
/// so it follows the app if it is ever installed somewhere else.
/// </remarks>
internal sealed class StartupRegistration : ILaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "CanvasCountdown";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string)
            {
                return false;
            }
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // First byte 0x02 (or no entry at all) means enabled; 0x03 means
            // the user switched it off in Task Manager.
            return approved?.GetValue(ValueName) is not byte[] state || state.Length == 0 || (state[0] & 0x01) == 0;
        }
    }

    public void SetEnabled(bool isEnabled)
    {
        if (isEnabled)
        {
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                run.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            approved.SetValue(ValueName, new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }
        else
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            {
                run?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Keeps the registered command pointing at this copy of the app.</summary>
    public void RefreshPathIfEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
        {
            run.SetValue(ValueName, Command, RegistryValueKind.String);
        }
    }
}
