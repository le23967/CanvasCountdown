using System.Diagnostics;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// Hands a downloaded, verified installer the job of replacing this copy.
/// </summary>
/// <remarks>
/// The installer runs silently and is told to relaunch the app when it is
/// done. It sets the installed folder aside rather than overwriting it, so if
/// anything fails part-way it puts the previous version back and starts that
/// instead (with <c>--update-failed</c>, so the app can say so). This app quits
/// as soon as the installer has started, so nothing it has open is in the way.
///
/// The installer's own log goes to the app's log folder, where it can be read
/// if an update ever does not take.
/// </remarks>
internal sealed class InstallerUpdateInstaller(Action quit) : IUpdateInstaller
{
    public void InstallAndRelaunch(string installerPath, string expectedSha256)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The downloaded update is missing. Try downloading it again.", installerPath);
        }
        string actual;
        using (var stream = File.OpenRead(installerPath))
        {
            actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        if (string.IsNullOrEmpty(expectedSha256) || !string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            // Closed first: Windows will not delete a file that is still open.
            File.Delete(installerPath);
            throw new InvalidOperationException("The downloaded update changed after it was checked, so it was deleted and not run. Try again.");
        }

        Directory.CreateDirectory(AppEnvironment.LogDirectory);
        var log = Path.Combine(AppEnvironment.LogDirectory, $"update-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        var start = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!,
        };
        foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH", $"/LOG={log}" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The update could not be started. Nothing was changed.");
        quit();
    }
}
