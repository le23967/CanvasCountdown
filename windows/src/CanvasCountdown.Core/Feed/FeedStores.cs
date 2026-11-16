namespace CanvasCountdown;

public enum FeedUrlValidationError
{
    HttpsRequired,
    MissingHost,
    EmbeddedCredentialsNotAllowed,
}

public sealed class FeedUrlValidationException(FeedUrlValidationError error) : Exception(Describe(error))
{
    public FeedUrlValidationError Error { get; } = error;

    public string DiagnosticCode => Error switch
    {
        FeedUrlValidationError.HttpsRequired => "url.https-required",
        FeedUrlValidationError.MissingHost => "url.missing-host",
        _ => "url.embedded-credentials",
    };

    private static string Describe(FeedUrlValidationError error) => error switch
    {
        FeedUrlValidationError.HttpsRequired => "The Canvas calendar address must use HTTPS.",
        FeedUrlValidationError.MissingHost => "Enter a complete Canvas calendar address.",
        _ => "The calendar address must not contain embedded sign-in credentials.",
    };
}

/// <summary>What every feed address is checked against before it is used or stored.</summary>
public static class FeedUrlValidator
{
    public static Uri Validated(Uri url)
    {
        if (!url.IsAbsoluteUri || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new FeedUrlValidationException(FeedUrlValidationError.HttpsRequired);
        }
        if (string.IsNullOrEmpty(url.Host))
        {
            throw new FeedUrlValidationException(FeedUrlValidationError.MissingHost);
        }
        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            throw new FeedUrlValidationException(FeedUrlValidationError.EmbeddedCredentialsNotAllowed);
        }
        return url;
    }
}

/// <summary>The private Canvas feed address. It is a password in all but name.</summary>
public interface IFeedUrlStore
{
    Task SaveFeedUrlAsync(Uri url);

    Task<Uri?> LoadFeedUrlAsync();

    Task DeleteFeedUrlAsync();
}

public enum FeedUrlStoreError
{
    StoredValueIsInvalid,
}

public sealed class FeedUrlStoreException(FeedUrlStoreError error) : Exception("The saved calendar address is invalid. Enter it again.")
{
    public FeedUrlStoreError Error { get; } = error;

    public string DiagnosticCode => "secret.invalid-stored-value";
}

/// <summary>
/// Keeps the feed address in the secret store (Credential Manager on Windows),
/// never in preferences, the database or diagnostics. Only an address that
/// passes <see cref="FeedUrlValidator"/> is ever stored or handed back.
/// </summary>
public sealed class SecretFeedUrlStore(ISecretStore secrets, string account = SecretAccounts.CanvasCalendarFeed) : IFeedUrlStore
{
    public Task SaveFeedUrlAsync(Uri url) => secrets.WriteAsync(account, FeedUrlValidator.Validated(url).AbsoluteUri);

    public async Task<Uri?> LoadFeedUrlAsync()
    {
        var stored = await secrets.ReadAsync(account);
        if (stored is null)
        {
            return null;
        }
        if (!Uri.TryCreate(stored, UriKind.Absolute, out var url))
        {
            throw new FeedUrlStoreException(FeedUrlStoreError.StoredValueIsInvalid);
        }
        try
        {
            return FeedUrlValidator.Validated(url);
        }
        catch (FeedUrlValidationException)
        {
            throw new FeedUrlStoreException(FeedUrlStoreError.StoredValueIsInvalid);
        }
    }

    public Task DeleteFeedUrlAsync() => secrets.DeleteAsync(account);
}

/// <summary>
/// Canvas UIDs explicitly deselected in the import preview. UIDs are not
/// credentials and the feed address is never part of this store. New,
/// previously unseen UIDs remain eligible for automatic import.
/// </summary>
public interface IFeedExclusionStore
{
    Task<ValueSet<string>> LoadExcludedUidsAsync();

    Task SaveExcludedUidsAsync(IEnumerable<string> uids);

    Task ClearExcludedUidsAsync();
}

public sealed class PreferencesFeedExclusionStore(IPreferencesStore preferences, string key = "CanvasCountdown.feed.excludedUIDs") : IFeedExclusionStore
{
    public Task<ValueSet<string>> LoadExcludedUidsAsync()
    {
        var stored = preferences.Get<string[]>(key, []);
        return Task.FromResult(stored.Select(SwiftText.Identity).Where(uid => uid.Length > 0).ToValueSet());
    }

    public Task SaveExcludedUidsAsync(IEnumerable<string> uids)
    {
        preferences.Set(key, uids.Select(SwiftText.Identity).Where(uid => uid.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return Task.CompletedTask;
    }

    public Task ClearExcludedUidsAsync()
    {
        preferences.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>
/// One spelling rule, shared by everything that compares course names, so a
/// block written from Settings still matches what the parser produces.
/// </summary>
/// <remarks>
/// Only ever used for comparison. What gets stored and shown is the name as it
/// was written, because "68101 physics lab" appearing in Settings under a
/// course the user knows as "68101 Physics Lab" reads like the app mangled it.
/// </remarks>
public static class CourseName
{
    public static string? Normalized(string? courseName)
    {
        if (courseName is null)
        {
            return null;
        }
        var trimmed = SwiftText.Lowercased(SwiftText.Trimmed(courseName));
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>The name as written, trimmed. Null when there is nothing left of it.</summary>
    public static string? Display(string? courseName)
    {
        if (courseName is null)
        {
            return null;
        }
        var trimmed = SwiftText.Trimmed(courseName);
        return trimmed.Length == 0 ? null : trimmed;
    }
}

/// <summary>
/// Courses the user removed, so a Canvas refresh cannot bring them back.
/// </summary>
/// <remarks>
/// A Canvas Calendar Feed carries every course the account is enrolled in,
/// including ones finished long ago. Deleting those events alone would not
/// hold: the next refresh imports them again. Blocking the course name is what
/// makes the removal stick, and unblocking it makes the removal reversible.
/// </remarks>
public interface ICourseBlocklist
{
    /// <summary>The blocked courses as the user saw them written.</summary>
    Task<ValueSet<string>> LoadBlockedCoursesAsync();

    Task BlockAsync(string courseName);

    Task UnblockAsync(string courseName);

    Task ClearBlockedCoursesAsync();
}

public static class CourseBlocklistExtensions
{
    /// <summary>True when a course, however the caller spelled it, is blocked.</summary>
    public static async Task<bool> IsBlockedAsync(this ICourseBlocklist blocklist, string? courseName)
        => CourseName.Normalized(courseName) is { } key && (await blocklist.BlockedCourseKeysAsync()).Contains(key);

    /// <summary>The set to match feed events against.</summary>
    public static async Task<ValueSet<string>> BlockedCourseKeysAsync(this ICourseBlocklist blocklist)
        => (await blocklist.LoadBlockedCoursesAsync()).Select(CourseName.Normalized).OfType<string>().ToValueSet();
}

/// <summary>The blocklist in preferences; the same list in memory for an isolated launch.</summary>
public sealed class PreferencesCourseBlocklistStore(IPreferencesStore preferences, string key = "CanvasCountdown.courses.blocked") : ICourseBlocklist
{
    private readonly object gate = new();

    public Task<ValueSet<string>> LoadBlockedCoursesAsync()
    {
        lock (gate)
        {
            return Task.FromResult(Load());
        }
    }

    public Task BlockAsync(string courseName)
    {
        lock (gate)
        {
            if (CourseName.Display(courseName) is { } name && CourseName.Normalized(courseName) is { } normalized)
            {
                Save(Load().Where(existing => CourseName.Normalized(existing) != normalized).Add(name));
            }
        }
        return Task.CompletedTask;
    }

    public Task UnblockAsync(string courseName)
    {
        lock (gate)
        {
            if (CourseName.Normalized(courseName) is { } normalized)
            {
                Save(Load().Where(existing => CourseName.Normalized(existing) != normalized));
            }
        }
        return Task.CompletedTask;
    }

    public Task ClearBlockedCoursesAsync()
    {
        lock (gate)
        {
            preferences.Remove(key);
        }
        return Task.CompletedTask;
    }

    private ValueSet<string> Load()
        => preferences.Get<string[]>(key, []).Select(CourseName.Display).OfType<string>().ToValueSet();

    private void Save(ValueSet<string> blocked) => preferences.Set(key, blocked.Sorted().ToArray());
}
