using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// Reads the text in a screenshot with Windows' own recogniser.
/// </summary>
/// <remarks>
/// Everything happens on this PC: the image is decoded in memory, read, and
/// let go, and neither it nor its text is written anywhere. What the text
/// means — which line is a title, which is a due date — is decided by the
/// parser in the core, which is tested; this only turns pixels into lines.
///
/// Canvas pages are read in English, so an English recogniser is used when
/// Windows has one, whatever the display language; otherwise the user's own
/// languages are tried. Images larger than the recogniser accepts are scaled
/// down first, keeping their shape, and boxes are measured against the image
/// that was actually read.
/// </remarks>
internal sealed class WindowsOcrRecognizer : IScreenshotOcrServicing
{
    public async Task<IReadOnlyList<OcrTextObservation>> RecognizeTextAsync(
        ScreenshotSource source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = CreateEngine();
        if (engine is null)
        {
            CrashLog.Note("No text recognition language is installed; screenshot import cannot read text.");
            throw new ScreenshotOcrException(ScreenshotOcrError.RecognitionFailed, source.DisplayName);
        }

        using var bitmap = await DecodeAsync(source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        OcrResult result;
        try
        {
            result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ScreenshotOcrException(ScreenshotOcrError.RecognitionFailed, source.DisplayName, exception);
        }

        double width = bitmap.PixelWidth;
        double height = bitmap.PixelHeight;
        return result.Lines
            .Where(line => line.Words.Count > 0 && !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => new OcrTextObservation
            {
                Text = line.Text,
                // Windows does not report a confidence; unknown is not low.
                Confidence = null,
                BoundingBox = NormalisedRect.Enclosing(line.Words.Select(word => NormalisedRect.FromPixels(
                    word.BoundingRect.X,
                    word.BoundingRect.Y,
                    word.BoundingRect.Width,
                    word.BoundingRect.Height,
                    width,
                    height))),
                ScreenshotId = source.Id,
            })
            .ToArray();
    }

    private static OcrEngine? CreateEngine()
    {
        var english = OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(language => language.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        return english is not null
            ? OcrEngine.TryCreateFromLanguage(english)
            : OcrEngine.TryCreateFromUserProfileLanguages();
    }

    private static async Task<SoftwareBitmap> DecodeAsync(ScreenshotSource source, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(source.ImageData.ToArray().AsBuffer()).AsTask(cancellationToken);
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

            var width = decoder.OrientedPixelWidth;
            var height = decoder.OrientedPixelHeight;
            if (width == 0 || height == 0)
            {
                throw new ScreenshotOcrException(ScreenshotOcrError.UnreadableImage, source.DisplayName);
            }
            if (width > ScreenshotOcrLimits.AbsolutePixelLimit || height > ScreenshotOcrLimits.AbsolutePixelLimit)
            {
                throw new ScreenshotOcrException(ScreenshotOcrError.ImageTooLarge, source.DisplayName);
            }

            var largestSide = Math.Max(width, height);
            var limit = Math.Min(ScreenshotOcrLimits.MaximumPixelDimension, OcrEngine.MaxImageDimension);
            var scale = largestSide > limit ? limit / (double)largestSide : 1.0;
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(width * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(height * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ScreenshotOcrException)
        {
            throw new ScreenshotOcrException(ScreenshotOcrError.UnreadableImage, source.DisplayName, exception);
        }
    }
}
