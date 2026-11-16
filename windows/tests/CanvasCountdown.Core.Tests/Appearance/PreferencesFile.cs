namespace CanvasCountdown.Tests.Appearance;

/// <summary>
/// A preferences file of its own for one test, so "survives a relaunch" can be
/// tested by opening it again — and nothing the user has is ever touched.
/// </summary>
internal sealed class PreferencesFile : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cc-appearance-" + Guid.NewGuid().ToString("N"));

    public string FilePath => Path.Combine(directory, "settings.json");

    /// <summary>A store over the file, as a fresh launch would open it.</summary>
    public JsonPreferencesStore Open() => new(FilePath);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
