using System.Collections.Concurrent;

namespace CanvasCountdown.Tests.Screenshots;

/// <summary>
/// Runs a test the way the window runs the view model: every continuation on
/// one thread, in order. Without it, a reading that finishes on another thread
/// could interleave with the test's own calls and make the cancellation tests
/// pass or fail by timing.
/// </summary>
internal sealed class UiThread : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];

    public static void Run(Func<Task> test)
    {
        var previous = Current;
        var context = new UiThread();
        SetSynchronizationContext(context);
        try
        {
            var task = test();
            task.ContinueWith(_ => context.queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context.queue.GetConsumingEnumerable())
            {
                callback(state);
            }
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public override SynchronizationContext CreateCopy() => this;
}

/// <summary>
/// A recogniser that holds each reading open until the test releases it or the
/// reading is cancelled, so a test can act while recognition is under way.
/// Screenshots it is not told to hold read at once.
/// </summary>
internal sealed class GatedOcrService : IScreenshotOcrServicing
{
    private readonly ValueList<OcrTextObservation> observations;
    private readonly ValueSet<string> heldNames;
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource holding = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GatedOcrService(IEnumerable<OcrTextObservation> observations, IEnumerable<string> heldNames)
    {
        this.observations = observations.ToValueList();
        this.heldNames = heldNames.ToValueSet();
    }

    /// <summary>Completes once a held screenshot has started reading and is waiting.</summary>
    public Task Holding => holding.Task;

    public void Release() => release.TrySetResult();

    public async Task<IReadOnlyList<OcrTextObservation>> RecognizeTextAsync(
        ScreenshotSource source,
        CancellationToken cancellationToken = default)
    {
        if (heldNames.Contains(source.DisplayName))
        {
            holding.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
        return observations.Select(observation => observation with { ScreenshotId = source.Id }).ToArray();
    }
}

/// <summary>Invented screenshot rows, as a recogniser would return them.</summary>
internal static class ScreenshotFixtures
{
    public static readonly AppCalendar Sydney = AppCalendar.ForTimeZone("Australia/Sydney");

    public static DateTimeOffset ReferenceNow => Sydney.FromLocal(2026, 7, 28, 9, 0);

    public static OcrTextObservation Line(string text, double y, double x = 0.08, double? confidence = 0.92)
        => new()
        {
            Text = text,
            Confidence = confidence,
            BoundingBox = new NormalisedRect(x, y, 0.5, 0.012),
        };

    /// <summary>Two rows: one with an availability window before its deadline, one with no due line at all.</summary>
    public static ValueList<OcrTextObservation> QuizAndUngradedTask =>
    [
        Line("Example Quiz", 0.10),
        Line("Not available until 4 Aug at 14:40", 0.125),
        Line("Due 4 Aug at 16:00", 0.145),
        Line("Example Ungraded Task", 0.25),
        Line("Not available until 4 Aug at 14:40", 0.275),
    ];

    public static ValueList<OcrTextObservation> QuizAndReflection =>
    [
        Line("Example Quiz", 0.10),
        Line("Due 4 Aug at 16:00", 0.125),
        Line("Example Reflection", 0.25),
        Line("Due 6 Sep at 23:59", 0.275),
    ];
}

/// <summary>Screenshot files on disk for the loading tests, removed afterwards.</summary>
internal sealed class TemporaryFiles : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cc-screenshots-" + Guid.NewGuid().ToString("N"));

    public TemporaryFiles()
    {
        Directory.CreateDirectory(directory);
    }

    public string Write(string name, byte[]? contents = null)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, contents ?? [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        return path;
    }

    /// <summary>A file of the given length that takes no time to make.</summary>
    public string WriteSized(string name, long length)
    {
        var path = Path.Combine(directory, name);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(length);
        return path;
    }

    public string PathFor(string name) => Path.Combine(directory, name);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
