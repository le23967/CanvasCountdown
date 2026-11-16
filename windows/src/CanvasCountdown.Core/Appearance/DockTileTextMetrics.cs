using System.Globalization;

namespace CanvasCountdown;

/// <summary>
/// How big the tile's text is, measured in the face that will draw it.
/// </summary>
/// <remarks>
/// The layout fits the number by measuring it, so it has to measure the font
/// the renderer actually uses: a face with wider digits than the one measured
/// would overflow its band. A Windows renderer implements this with the same
/// text API it draws with (DirectWrite measures what it will draw).
/// <see cref="DockTileTextMetrics.Reference"/> reproduces the Mac's font, so
/// the layout can be computed and tested without any font engine.
/// </remarks>
public interface IDockTileTextMetrics
{
    /// <summary>
    /// The advance width of the countdown drawn as one run with no added
    /// tracking: the glyph advances plus any kerning between them.
    /// </summary>
    double CountdownWidth(string text, double fontSize, DockNumberWeight weight);

    double CountdownCapHeight(double fontSize, DockNumberWeight weight);

    /// <summary>
    /// The distance from the top of a line to its baseline: where
    /// <see cref="DockTileText.Origin"/> puts the top of the countdown's line.
    /// </summary>
    double CountdownAscender(double fontSize, DockNumberWeight weight);

    /// <summary>
    /// The header's size in the bold header face, with
    /// <paramref name="tracking"/> added after every character, the last one
    /// included: its width, and the height of its line.
    /// </summary>
    TileSize LabelSize(string text, double fontSize, double tracking);
}

public static class DockTileTextMetrics
{
    /// <summary>
    /// Metrics of the Mac's rounded system font, measured at 100 points: digit
    /// advances and the kerning between digits for both weights, the header's
    /// capital letters, and the vertical metrics.
    /// </summary>
    /// <remarks>
    /// At the sizes a 128-pixel tile draws, these reproduce the Mac's layout
    /// to the pixel. At very small sizes the Mac's font tracks a little wider
    /// than its 100-point shape, which these do not model. Characters the
    /// table does not hold are measured as 0.6 of the font size, or 0.94 for
    /// ideographs.
    /// </remarks>
    public static IDockTileTextMetrics Reference { get; } = new ReferenceMetrics();

    private sealed class ReferenceMetrics : IDockTileTextMetrics
    {
        private const double CapHeightRatio = 0.70458984375;
        private const double AscenderRatio = 0.966796875;
        private const double DescenderRatio = 0.2109375;
        private const double FallbackAdvance = 0.6;
        private const double IdeographAdvance = 0.943609022556;
        private const string DigitsAndDash = "0123456789–";

        private static readonly double[] BoldDigitAdvances =
        [
            0.655517578125, 0.479980468750, 0.603027343750, 0.629638671875, 0.647460937500,
            0.625000000000, 0.642822265625, 0.572021484375, 0.652343750000, 0.644775390625,
            0.500000000000,
        ];

        private static readonly double[] HeavyDigitAdvances =
        [
            0.681425482035, 0.499314725399, 0.622619390488, 0.649617403746, 0.670146465302,
            0.646138787270, 0.666152268648, 0.585297673941, 0.680442869663, 0.669136554003,
            0.500000000000,
        ];

        private static readonly Dictionary<string, double> BoldKerning = new(StringComparer.Ordinal)
        {
            ["01"] = -0.004882812500, ["02"] = -0.004882812500, ["05"] = -0.004882812500, ["07"] = -0.017089843750,
            ["19"] = -0.002441406250, ["20"] = -0.002441406250, ["24"] = -0.012207031250, ["26"] = -0.002441406250,
            ["30"] = -0.002441406250, ["36"] = -0.002441406250, ["40"] = -0.002441406250, ["41"] = -0.004882812500,
            ["46"] = -0.002441406250, ["47"] = -0.009765625000, ["70"] = -0.007324218750, ["74"] = -0.053710937500,
            ["76"] = -0.007324218750, ["80"] = -0.002441406250, ["86"] = -0.002441406250, ["91"] = -0.004882812500,
            ["92"] = -0.004882812500, ["95"] = -0.004882812500, ["97"] = -0.017089843750,
        };

        private static readonly Dictionary<string, double> HeavyKerning = new(StringComparer.Ordinal)
        {
            ["01"] = -0.002304911613, ["02"] = -0.002304911613, ["05"] = -0.007460713387, ["07"] = -0.015800893307,
            ["19"] = -0.003730356693, ["20"] = -0.003730356693, ["24"] = -0.005762279034, ["26"] = -0.003730356693,
            ["30"] = -0.003730356693, ["36"] = -0.003730356693, ["40"] = -0.003730356693, ["41"] = -0.007460713387,
            ["46"] = -0.003730356693, ["47"] = -0.009765625000, ["70"] = -0.006035268307, ["74"] = -0.045977234840,
            ["76"] = -0.006035268307, ["80"] = -0.003730356693, ["86"] = -0.003730356693, ["91"] = -0.002304911613,
            ["92"] = -0.002304911613, ["95"] = -0.007460713387, ["97"] = -0.015800893307,
        };

        /// <summary>Capitals A to Z of the bold header face.</summary>
        private static readonly double[] LabelCapitalAdvances =
        [
            0.689941406250, 0.639648437500, 0.712402343750, 0.699707031250, 0.572998046875, 0.548095703125,
            0.726806640625, 0.729492187500, 0.264648437500, 0.557861328125, 0.660400390625, 0.542968750000,
            0.863037109375, 0.714355468750, 0.752441406250, 0.617431640625, 0.752197265625, 0.640136718750,
            0.631347656250, 0.601562500000, 0.707519531250, 0.678222656250, 0.965332031250, 0.685302734375,
            0.665039062500, 0.630859375000,
        ];

        private const double LabelSpaceAdvance = 0.197753906250;

        public double CountdownWidth(string text, double fontSize, DockNumberWeight weight)
        {
            var advances = weight == DockNumberWeight.Bold ? BoldDigitAdvances : HeavyDigitAdvances;
            var kerning = weight == DockNumberWeight.Bold ? BoldKerning : HeavyKerning;
            var total = 0.0;
            for (var index = 0; index < text.Length; index++)
            {
                var position = DigitsAndDash.IndexOf(text[index], StringComparison.Ordinal);
                total += position >= 0 ? advances[position] : FallbackAdvance;
                if (index > 0 && kerning.TryGetValue(text.Substring(index - 1, 2), out var adjustment))
                {
                    total += adjustment;
                }
            }
            return total * fontSize;
        }

        public double CountdownCapHeight(double fontSize, DockNumberWeight weight) => CapHeightRatio * fontSize;

        public double CountdownAscender(double fontSize, DockNumberWeight weight) => AscenderRatio * fontSize;

        public TileSize LabelSize(string text, double fontSize, double tracking)
        {
            var width = 0.0;
            var characters = StringInfo.GetTextElementEnumerator(text);
            while (characters.MoveNext())
            {
                width += (LabelAdvance(characters.GetTextElement()) * fontSize) + tracking;
            }
            return new TileSize(width, (AscenderRatio + DescenderRatio) * fontSize);
        }

        private static double LabelAdvance(string character)
        {
            if (character.Length != 1)
            {
                return FallbackAdvance;
            }
            var single = character[0];
            if (single is >= 'A' and <= 'Z')
            {
                return LabelCapitalAdvances[single - 'A'];
            }
            if (single is >= '0' and <= '9')
            {
                return BoldDigitAdvances[single - '0'];
            }
            if (single == ' ')
            {
                return LabelSpaceAdvance;
            }
            return IsIdeograph(single) ? IdeographAdvance : FallbackAdvance;
        }

        private static bool IsIdeograph(char character)
            => character is (>= '⺀' and <= '鿿') or (>= '豈' and <= '﫿') or (>= '＀' and <= '￯');
    }
}
