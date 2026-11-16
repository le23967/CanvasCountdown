namespace CanvasCountdown;

/// <summary>
/// Secrets by account name: the private Canvas feed URL and the AI service
/// keys. On Windows these go to Credential Manager; nothing secret is ever
/// written to preferences, the database or diagnostics.
/// </summary>
/// <remarks>
/// The account names are kept from the macOS Keychain items
/// (<see cref="SecretAccounts"/>) so the two versions describe the same
/// things the same way.
/// </remarks>
public interface ISecretStore
{
    /// <summary>The secret, or null when none is stored.</summary>
    Task<string?> ReadAsync(string account);

    Task WriteAsync(string account, string secret);

    /// <summary>Removes the secret. Removing one that is not there is not an error.</summary>
    Task DeleteAsync(string account);
}

public static class SecretAccounts
{
    public const string CanvasCalendarFeed = "canvas-calendar-feed";
    public const string AssistantApiKey = "assistant-api-key";
}

/// <summary>
/// A failure of the secret store itself. The message never contains the
/// secret or the account query; <see cref="DiagnosticCode"/> is safe to put in
/// exported diagnostics.
/// </summary>
public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string message, string diagnosticCode)
        : base(message)
    {
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

/// <summary>
/// Holds secrets in memory only, so a test or an isolated launch cannot read
/// or rewrite the user's real Credential Manager entries.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public Task<string?> ReadAsync(string account)
    {
        lock (gate)
        {
            return Task.FromResult(secrets.TryGetValue(account, out var secret) ? secret : null);
        }
    }

    public Task WriteAsync(string account, string secret)
    {
        lock (gate)
        {
            secrets[account] = secret;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account)
    {
        lock (gate)
        {
            secrets.Remove(account);
        }
        return Task.CompletedTask;
    }
}
