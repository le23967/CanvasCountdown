using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CanvasCountdown;

/// <summary>
/// A version number, compared the way people expect rather than the way
/// strings sort.
/// </summary>
/// <remarks>
/// "1.10.0" is newer than "1.9.0"; as text it is not. Getting that backwards
/// would leave everyone on 1.9.0 being told they are up to date for the rest of
/// the app's life, which is the one failure an update check cannot recover from
/// on its own.
/// </remarks>
public sealed class AppVersion : IEquatable<AppVersion>, IComparable<AppVersion>, IComparable
{
    /// <summary>Zero-padded to three, so "1.2" and "1.2.0" are the same version.</summary>
    private readonly long[] components;

    private readonly string description;

    private AppVersion(long[] components, string description)
    {
        this.components = components;
        this.description = description;
    }

    /// <summary>
    /// Reads "1.2.0", "v1.2.0" or "1.2". Anything that is not a run of numbers
    /// separated by dots is not a version this app produces, and is refused —
    /// null, not an exception — rather than guessed at.
    /// </summary>
    public static AppVersion? Parse(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var trimmed = FoundationText.TrimmingWhitespaceAndNewlines(text);
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }
        if (trimmed.Length == 0)
        {
            return null;
        }
        var parts = trimmed.Split('.');
        if (parts.Length > 4)
        {
            return null;
        }
        var numbers = new List<long>(Math.Max(3, parts.Length));
        foreach (var part in parts)
        {
            if (part.Length == 0
                || !part.All(char.IsAsciiDigit)
                || !long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }
            numbers.Add(value);
        }
        while (numbers.Count < 3)
        {
            numbers.Add(0);
        }
        return new AppVersion([.. numbers], trimmed);
    }

    /// <summary>
    /// The version of the running app, read from its own assembly: the
    /// informational version without any build metadata after a "+", falling
    /// back to the assembly version.
    /// </summary>
    /// <remarks>
    /// Pass the app's own assembly. Under a test runner the entry assembly is
    /// the runner, whose version has nothing to do with the app's.
    /// </remarks>
    public static AppVersion? Current(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return FromInformationalVersion(informational);
        }
        return assembly.GetName().Version is { } version
            ? Parse(string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}"))
            : null;
    }

    /// <summary>"1.2.0+4f1c2e9" reads as 1.2.0: the build metadata is not part of the version.</summary>
    internal static AppVersion? FromInformationalVersion(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        return Parse(plus >= 0 ? text[..plus] : text);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        for (var index = 0; index < Math.Max(components.Length, other.components.Length); index++)
        {
            var left = index < components.Length ? components[index] : 0;
            var right = index < other.components.Length ? other.components[index] : 0;
            if (left != right)
            {
                return left < right ? -1 : 1;
            }
        }
        return 0;
    }

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        AppVersion other => CompareTo(other),
        _ => throw new ArgumentException("Not a version.", nameof(obj)),
    };

    public bool Equals(AppVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);

    public override int GetHashCode()
    {
        var significant = components.Length;
        while (significant > 0 && components[significant - 1] == 0)
        {
            significant--;
        }
        var hash = new HashCode();
        for (var index = 0; index < significant; index++)
        {
            hash.Add(components[index]);
        }
        return hash.ToHashCode();
    }

    /// <summary>The text it was read from, without a leading "v": "1.10.0".</summary>
    public override string ToString() => description;

    public static bool operator ==(AppVersion? left, AppVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(AppVersion? left, AppVersion? right) => !(left == right);

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;
}

/// <summary>The processor the installer is built for.</summary>
public enum UpdateArchitecture
{
    X64,
    Arm64,
}

public static class UpdateArchitectureExtensions
{
    /// <summary>How the architecture is spelled in an installer's name.</summary>
    public static string InstallerToken(this UpdateArchitecture architecture) => architecture switch
    {
        UpdateArchitecture.X64 => "x64",
        UpdateArchitecture.Arm64 => "arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
    };

    /// <summary>
    /// The installer for a processor architecture, or null for one no
    /// installer is built for.
    /// </summary>
    /// <remarks>
    /// Pass <c>RuntimeInformation.OSArchitecture</c> rather than the process
    /// architecture: an x64 copy running under emulation on an Arm PC is then
    /// offered the native build.
    /// </remarks>
    public static UpdateArchitecture? From(Architecture architecture) => architecture switch
    {
        Architecture.X64 => UpdateArchitecture.X64,
        Architecture.Arm64 => UpdateArchitecture.Arm64,
        _ => null,
    };
}

/// <summary>
/// The installer for this PC in a release, with what it must be: its size and
/// SHA-256 digest as GitHub published them.
/// </summary>
public sealed record ReleaseInstaller
{
    /// <summary><c>CanvasCountdown-Windows-&lt;version&gt;-&lt;arch&gt;-setup.exe</c>.</summary>
    public required string FileName { get; init; }

    public required Uri Url { get; init; }

    /// <summary>In bytes.</summary>
    public required long Size { get; init; }

    /// <summary>Lower-case hexadecimal, 64 characters.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>A published release, as much of it as this app has any use for.</summary>
public sealed record AppRelease
{
    public required AppVersion Version { get; init; }

    /// <summary>The tag, kept as published so a link built from it cannot be wrong.</summary>
    public required string Tag { get; init; }

    public required string Name { get; init; }

    /// <summary>The release notes, as written. Shown in full only when asked for.</summary>
    public required string Notes { get; init; }

    /// <summary>
    /// The installer for this PC. Absent when a release was published without
    /// one — or without the digest that proves a download is the published
    /// file — which is a release nobody can install from here.
    /// </summary>
    public ReleaseInstaller? Installer { get; init; }

    public Uri? DownloadUrl => Installer?.Url;

    /// <summary>
    /// The page to open when there is nothing to download, or when someone
    /// would rather read first.
    /// </summary>
    public required Uri PageUrl { get; init; }

    /// <summary>
    /// The first line or two of the notes, for a banner that must not become a
    /// wall of text.
    /// </summary>
    public string Summary
    {
        get
        {
            var firstParagraph = Notes
                .Split("\n\n")
                .Select(FoundationText.TrimmingWhitespaceAndNewlines)
                .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("Copyright", StringComparison.Ordinal));
            if (firstParagraph is null)
            {
                return string.Empty;
            }
            // Markdown marks read as noise in one line of a banner: nothing here
            // renders them, so they arrive as literal asterisks and backticks.
            var flattened = FoundationText.TrimmingWhitespace(firstParagraph
                .Replace("\n", " ", StringComparison.Ordinal)
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal)
                .Replace("#", string.Empty, StringComparison.Ordinal));
            if (FoundationText.CharacterCount(flattened) <= 150)
            {
                return flattened;
            }
            return FoundationText.TrimmingWhitespaceAndNewlines(FoundationText.Prefix(flattened, 149)) + "…";
        }
    }
}
