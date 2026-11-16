using static CanvasCountdown.Tests.Screenshots.ScreenshotFixtures;

namespace CanvasCountdown.Tests.Screenshots;

/// <summary>
/// Not in the macOS suite: the recogniser boundary the Windows port defines.
/// Boxes arrive in one convention whatever produced them, reading order is the
/// parser's to decide, and a recogniser that cannot say how sure it is is not
/// taken to be unsure.
/// </summary>
public sealed class OcrBoundaryTests
{
    private readonly CanvasScreenshotParser parser = new();

    private ValueList<ScreenshotImportCandidate> Parse(IReadOnlyList<OcrTextObservation> observations)
        => parser.Candidates(observations, "example-screenshot.png", ReferenceNow, Sydney);

    [Fact]
    public void PixelRectanglesAreMeasuredAgainstTheImage()
    {
        var box = NormalisedRect.FromPixels(128, 80, 640, 12, imageWidth: 1280, imageHeight: 800);

        Assert.Equal(new NormalisedRect(0.1, 0.1, 0.5, 0.015), box);
    }

    [Fact]
    public void BottomLeftBoxesAreFlippedToReadFromTheTop()
    {
        // Vision's convention: y is the bottom edge, measured up from the
        // bottom of the image.
        var box = NormalisedRect.FromBottomLeftOrigin(0.08, 0.888, 0.5, 0.012);

        Assert.Equal(0.08, box.MinX);
        Assert.Equal(0.100, box.MinY, precision: 12);
        Assert.Equal(0.112, box.MaxY, precision: 12);
        Assert.Equal(0.5, box.Width);
    }

    [Fact]
    public void ALineEnclosesItsWords()
    {
        // Windows.Media.Ocr reports a rectangle per word; the line is the box
        // around all of them.
        NormalisedRect Word(double x, double y, double width, double height)
            => NormalisedRect.FromPixels(x, y, width, height, imageWidth: 1000, imageHeight: 1000);

        var line = NormalisedRect.Enclosing([Word(100, 200, 80, 20), Word(190, 198, 60, 24), Word(260, 201, 90, 18)]);

        Assert.Equal(0.100, line.MinX, precision: 12);
        Assert.Equal(0.198, line.MinY, precision: 12);
        Assert.Equal(0.350, line.MaxX, precision: 12);
        Assert.Equal(0.222, line.MaxY, precision: 12);
        Assert.Equal(NormalisedRect.Zero, NormalisedRect.Enclosing([]));
    }

    [Fact]
    public void ANegativeSizeCannotFlipABox()
    {
        var box = new NormalisedRect(0.6, 0.2, -0.5, -0.1);

        Assert.Equal(0.1, box.MinX, precision: 12);
        Assert.Equal(0.1, box.MinY, precision: 12);
        Assert.Equal(0.5, box.Width, precision: 12);
        Assert.Equal(0.2, box.MaxY, precision: 12);
    }

    [Fact]
    public void TheSameListGroupsTheSameAtAnyCaptureSize()
    {
        // One Canvas list captured at 1280×800 and again at one and a half
        // times the size: the pixel boxes differ, the rows must not.
        ValueList<ScreenshotImportCandidate> Read(double scale)
        {
            OcrTextObservation Pixels(string text, double top) => new()
            {
                Text = text,
                Confidence = null,
                BoundingBox = NormalisedRect.FromPixels(100 * scale, top * scale, 600 * scale, 10 * scale, 1280 * scale, 800 * scale),
            };
            return Parse(
            [
                Pixels("Example Quiz", 80),
                Pixels("Due 4 Aug at 16:00", 96),
                Pixels("Example Reflection", 200),
                Pixels("Due 6 Sep at 23:59", 216),
            ]);
        }

        var small = Read(1);
        var large = Read(1.5);

        Assert.Equal(["Example Quiz", "Example Reflection"], small.Select(candidate => candidate.Title));
        Assert.Equal(
            small.Select(candidate => (candidate.Title, candidate.DueDate)),
            large.Select(candidate => (candidate.Title, candidate.DueDate)));
    }

    [Fact]
    public void ReadingOrderIsTheParsersNotTheRecognisers()
    {
        // A recogniser may hand its lines back in any order. The rows found
        // must not change with it.
        ValueList<OcrTextObservation> lines =
        [
            Line("Example Poster", 0.100, x: 0.08),
            Line("Submission", 0.101, x: 0.40),
            Line("Not available until 16 Aug at 23:59", 0.125),
            Line("Due 30 Aug at 23:59", 0.145),
            Line("Example Reflection", 0.22),
            Line("10 pts", 0.24),
            Line("Due 6 Sep at 23:59", 0.26),
        ];
        var expected = Parse(lines).Select(candidate => (candidate.Title, candidate.DueDate)).ToArray();
        Assert.Equal(["Example Poster Submission", "Example Reflection"], expected.Select(row => row.Title));

        for (var seed = 1; seed <= 20; seed++)
        {
            var shuffled = lines.ToArray();
            new Random(seed).Shuffle(shuffled);
            Assert.Equal(expected, Parse(shuffled).Select(candidate => (candidate.Title, candidate.DueDate)));
        }
    }

    [Fact]
    public void LinesChainingWithinTheRowToleranceStillHaveOneOrder()
    {
        // Each line's top is within the tolerance of the next one's, but the
        // highest and lowest are not. Compared in pairs, left comes before
        // centre and centre before right (same row, by x), yet right comes
        // before left (different rows, by y): a cycle, with no consistent
        // answer. Anchored on its highest line, the row holds right and centre,
        // read left to right, and left starts the next row — whatever order the
        // lines came in.
        var right = Line("Right", 0.100, x: 0.30);
        var centre = Line("Centre", 0.103, x: 0.20);
        var left = Line("Left", 0.106, x: 0.10);

        OcrTextObservation[][] orders =
        [
            [right, centre, left], [right, left, centre], [centre, right, left],
            [centre, left, right], [left, right, centre], [left, centre, right],
        ];
        foreach (var order in orders)
        {
            Assert.Equal(["Centre", "Right", "Left"], CanvasScreenshotParser.ReadingOrder(order).Select(line => line.Text));
        }
    }

    [Fact]
    public void AReaderThatReportsNoConfidenceIsNotTakenToBeUnsure()
    {
        var candidate = Assert.Single(Parse(
        [
            Line("Example Quiz", 0.10, confidence: null),
            Line("Due 4 Aug 2026 at 16:00", 0.125, confidence: null),
        ]));

        Assert.Null(candidate.OcrConfidence);
        Assert.DoesNotContain(ScreenshotImportWarning.LowConfidence, candidate.Warnings);
        Assert.Equal(ScreenshotCandidateStatus.Ready, candidate.Status);
    }

    [Fact]
    public async Task AnInertReaderFindsNothingAndTheStubStampsEachScreenshot()
    {
        var first = new ScreenshotSource { DisplayName = "first.png", ImageData = new byte[] { 1 } };
        var second = new ScreenshotSource { DisplayName = "second.png", ImageData = new byte[] { 1 } };
        var stub = StubOcrService.Returning([Line("Example Quiz", 0.10)]);

        Assert.Empty(await new InertScreenshotOcrService().RecognizeTextAsync(first));
        Assert.Equal(first.Id, (await stub.RecognizeTextAsync(first))[0].ScreenshotId);
        Assert.Equal(second.Id, (await stub.RecognizeTextAsync(second))[0].ScreenshotId);
    }
}
