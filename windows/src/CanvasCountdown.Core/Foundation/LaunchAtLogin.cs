namespace CanvasCountdown;

/// <summary>Whether the app starts when the user signs in.</summary>
public interface ILaunchAtLogin
{
    bool IsEnabled { get; }

    /// <summary>Throws when the system refuses; the caller shows why and leaves the switch where it was.</summary>
    void SetEnabled(bool isEnabled);
}

/// <summary>
/// Remembers the choice in memory only, so an isolated launch can never add
/// or remove the user's real startup entry.
/// </summary>
public sealed class InMemoryLaunchAtLogin : ILaunchAtLogin
{
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;
}
