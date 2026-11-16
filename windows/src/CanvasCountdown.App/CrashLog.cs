namespace CanvasCountdown.App;

/// <summary>
/// Writes what went wrong when the app falls over, so a friend's "it just
/// closed" can be answered.
/// </summary>
/// <remarks>
/// Only the exception's type, message and stack are written. Messages in this
/// app never contain the feed URL or a key (those types redact themselves), but
/// URLs are still scrubbed here as a second line of defence, the same way the
/// exported diagnostics are.
/// </remarks>
public static class CrashLog
{
    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppEnvironment.LogDirectory);
            var path = Path.Combine(AppEnvironment.LogDirectory, "crash.log");
            var entry = $"[{DateTimeOffset.Now:O}] {AppEnvironment.Version}{Environment.NewLine}{Redact(exception.ToString())}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(path, entry);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Redact(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"https?://\S+", "<url redacted>");
}
