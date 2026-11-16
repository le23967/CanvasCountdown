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
    public static void Write(Exception exception) => Append("crash.log", exception.ToString());

    /// <summary>Something worth knowing that did not stop the app.</summary>
    public static void Note(string message) => Append("diagnostics.log", message);

    private static void Append(string fileName, string text)
    {
        try
        {
            Directory.CreateDirectory(AppEnvironment.LogDirectory);
            var path = Path.Combine(AppEnvironment.LogDirectory, fileName);
            var entry = $"[{DateTimeOffset.Now:O}] {AppEnvironment.Version}{Environment.NewLine}{Redact(text)}{Environment.NewLine}{Environment.NewLine}";
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
