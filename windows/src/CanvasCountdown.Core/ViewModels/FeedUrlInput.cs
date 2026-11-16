namespace CanvasCountdown;

public enum FeedUrlInputError
{
    Invalid,
    RequiresHttps,
}

public sealed class FeedUrlInputException(FeedUrlInputError error) : Exception(Describe(error))
{
    public FeedUrlInputError Error { get; } = error;

    private static string Describe(FeedUrlInputError error) => error switch
    {
        FeedUrlInputError.RequiresHttps => "For your security, the calendar feed URL must use HTTPS.",
        _ => "Enter a valid Canvas calendar feed URL.",
    };
}

/// <summary>What the import box accepts as a feed address.</summary>
public static class FeedUrlInput
{
    /// <summary>
    /// The address as typed or pasted, tidied: surrounding space removed, and a
    /// <c>webcal://</c> link (what Canvas's "subscribe" button hands out) read
    /// as the HTTPS address it stands for. Anything that is not HTTPS with a
    /// host is refused before a request is made.
    /// </summary>
    public static Uri Normalize(string text)
    {
        var value = text.Trim();
        if (value.Length == 0 || !Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            throw new FeedUrlInputException(FeedUrlInputError.Invalid);
        }
        if (string.Equals(parsed.Scheme, "webcal", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = parsed.IsDefaultPort ? -1 : parsed.Port };
            parsed = builder.Uri;
        }
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(parsed.Host))
        {
            throw new FeedUrlInputException(FeedUrlInputError.RequiresHttps);
        }
        return parsed;
    }
}
