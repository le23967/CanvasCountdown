using System.Globalization;

namespace CanvasCountdown;

/// <summary>Geometry and type sizing for the countdown tile.</summary>
/// <remarks>
/// Kept separate from the drawing code so the proportions can be asserted
/// directly: a small header band across the top, and a countdown numeral that
/// fills the middle of the tile at every size.
///
/// Sizing works from the numeral's cap height rather than its line box. A line
/// box carries ascender and descender space that digits never use, and sizing
/// against it leaves the number looking far smaller than the tile allows.
///
/// Every measurement goes through <see cref="Metrics"/>, which must describe
/// the face the tile is drawn in.
/// </remarks>
public sealed record DockTileLayout
{
    /// <summary>Fractions of the tile height.</summary>
    public static class Proportion
    {
        public const double Inset = 0.031;
        public const double CornerRadius = 0.18;
        public const double LabelTop = 0.05;
        public const double LabelHeight = 0.17;
        public const double CountdownTop = 0.24;
        public const double CountdownHeight = 0.66;
        public const double LabelFont = 0.135;
        public const double HorizontalPadding = 0.04;

        /// <summary>The header never competes with the number for attention.</summary>
        public const double LabelToCountdownCeiling = 0.38;

        /// <summary>Share of the number band the digits' cap height should fill.</summary>
        public const double DigitCapFill = 0.94;

        public const double PlaceholderCapFill = 0.46;
    }

    /// <summary>The dash (an en dash) shown when nothing is counting down.</summary>
    public const string Placeholder = "–";

    public const double MinimumFontSize = 4;

    private DockTileLayout()
    {
    }

    public TileRect TileRect { get; private init; }

    public double CornerRadius { get; private init; }

    /// <summary>The header band. Zero height in a compact layout, which has no header.</summary>
    public TileRect LabelRect { get; private init; }

    /// <summary>Zero in a compact layout, which has no header.</summary>
    public double LabelFontSize { get; private init; }

    public TileRect CountdownRect { get; private init; }

    public double CountdownFontSize { get; private init; }

    public DockNumberWeight NumberWeight { get; private init; }

    public IDockTileTextMetrics Metrics { get; private init; } = DockTileTextMetrics.Reference;

    public bool HasHeader => LabelRect.Height > 0;

    public static string Text(int? daysRemaining)
        => daysRemaining?.ToString(CultureInfo.InvariantCulture) ?? Placeholder;

    public static DockTileLayout Make(
        TileRect bounds,
        string countdownText,
        DockNumberSize numberSize = DockNumberSize.Medium,
        DockNumberWeight numberWeight = DockNumberWeight.Heavy,
        IDockTileTextMetrics? metrics = null)
    {
        metrics ??= DockTileTextMetrics.Reference;
        var inset = Math.Min(bounds.Width, bounds.Height) * Proportion.Inset;
        var tileRect = bounds.InsetBy(inset, inset);
        var height = tileRect.Height;
        var horizontalPadding = tileRect.Width * Proportion.HorizontalPadding;

        // The bands are fixed, so the header always keeps its room and the
        // number always sits in the same region whatever size is chosen.
        var labelRect = new TileRect(
            tileRect.MinX + horizontalPadding,
            tileRect.MinY + (height * Proportion.LabelTop),
            tileRect.Width - (horizontalPadding * 2),
            height * Proportion.LabelHeight);
        var countdownRect = new TileRect(
            tileRect.MinX + horizontalPadding,
            tileRect.MinY + (height * Proportion.CountdownTop),
            tileRect.Width - (horizontalPadding * 2),
            height * Proportion.CountdownHeight);

        // Fit first, then scale. Fitting alone would push every size preference
        // back to the same visual result, which is what made Small look no
        // different from Large.
        var safeFontSize = MaximumSafeFontSize(countdownText, countdownRect, numberWeight, metrics);
        var countdownFontSize = Math.Max(MinimumFontSize, safeFontSize * numberSize.FontScale());

        return new DockTileLayout
        {
            TileRect = tileRect,
            CornerRadius = Math.Min(tileRect.Width, height) * Proportion.CornerRadius,
            LabelRect = labelRect,
            LabelFontSize = Math.Min(
                Math.Max(5, height * Proportion.LabelFont),
                countdownFontSize * Proportion.LabelToCountdownCeiling),
            CountdownRect = countdownRect,
            CountdownFontSize = countdownFontSize,
            NumberWeight = numberWeight,
            Metrics = metrics,
        };
    }

    /// <summary>
    /// The tile at tray and taskbar-badge sizes: digits only, as large as fit.
    /// </summary>
    /// <remarks>
    /// A Windows addition. At 16 to 32 pixels the header could only be drawn a
    /// few pixels tall, and every pixel it takes is one the number cannot
    /// have, so the number band is the whole tile less its padding. The size
    /// preference is not applied here: it chooses how much of the room to
    /// use, and at this size anything less than all of it is less legible.
    /// The tile shape, corner radius and inset are the ones the full tile has.
    /// </remarks>
    public static DockTileLayout MakeCompact(
        TileRect bounds,
        string countdownText,
        DockNumberWeight numberWeight = DockNumberWeight.Heavy,
        IDockTileTextMetrics? metrics = null)
    {
        metrics ??= DockTileTextMetrics.Reference;
        var inset = Math.Min(bounds.Width, bounds.Height) * Proportion.Inset;
        var tileRect = bounds.InsetBy(inset, inset);
        var padding = tileRect.Width * Proportion.HorizontalPadding;
        var countdownRect = tileRect.InsetBy(padding, padding);

        return new DockTileLayout
        {
            TileRect = tileRect,
            CornerRadius = Math.Min(tileRect.Width, tileRect.Height) * Proportion.CornerRadius,
            LabelRect = new TileRect(countdownRect.MinX, countdownRect.MinY, countdownRect.Width, 0),
            LabelFontSize = 0,
            CountdownRect = countdownRect,
            CountdownFontSize = MaximumSafeFontSize(countdownText, countdownRect, numberWeight, metrics),
            NumberWeight = numberWeight,
            Metrics = metrics,
        };
    }

    /// <summary>The space the glyphs actually occupy: advance width by cap height.</summary>
    /// <remarks>
    /// Measured as one run with no tracking. Proportional rather than
    /// monospaced digits: monospaced digits pad every glyph out to the width of
    /// the widest one, which left a conspicuous gap either side of a narrow "1"
    /// — "17" read as two separate characters. The tile is redrawn at most once
    /// a day, so the column stability monospacing buys is not worth that gap.
    /// </remarks>
    public static TileSize CountdownInkSize(
        string text,
        double fontSize,
        DockNumberWeight weight = DockNumberWeight.Heavy,
        IDockTileTextMetrics? metrics = null)
    {
        metrics ??= DockTileTextMetrics.Reference;
        return new TileSize(metrics.CountdownWidth(text, fontSize, weight), metrics.CountdownCapHeight(fontSize, weight));
    }

    /// <summary>
    /// Top-left point of the countdown's line box, chosen so the digits are
    /// optically centred on their cap height inside the number band.
    /// </summary>
    public TilePoint CountdownOrigin(string text)
    {
        var ink = CountdownInkSize(text, CountdownFontSize, NumberWeight, Metrics);
        var capHeight = Metrics.CountdownCapHeight(CountdownFontSize, NumberWeight);
        var ascender = Metrics.CountdownAscender(CountdownFontSize, NumberWeight);
        return new TilePoint(
            CountdownRect.MidX - (ink.Width / 2),
            CountdownRect.MidY + (capHeight / 2) - ascender);
    }

    public TilePoint LabelOrigin(string text, TileSize measuredSize)
        => new(LabelRect.MidX - (measuredSize.Width / 2), LabelRect.MidY - (measuredSize.Height / 2));

    /// <summary>
    /// The largest size at which this value still fits the band in both
    /// directions, before the user's size preference is applied.
    /// </summary>
    /// <remarks>
    /// This is where digit-count adaptation happens: "1024" runs out of width
    /// long before "7" does, so it yields a smaller safe size.
    /// </remarks>
    public static double MaximumSafeFontSize(
        string text,
        TileRect band,
        DockNumberWeight weight = DockNumberWeight.Heavy,
        IDockTileTextMetrics? metrics = null)
    {
        if (!(band.Height > 0) || !(band.Width > 0))
        {
            return 1;
        }
        metrics ??= DockTileTextMetrics.Reference;

        var capRatio = Math.Max(0.4, metrics.CountdownCapHeight(100, weight) / 100);
        var fill = text == Placeholder ? Proportion.PlaceholderCapFill : Proportion.DigitCapFill;

        var size = Math.Max(MinimumFontSize, band.Height * fill / capRatio);
        while (size > MinimumFontSize)
        {
            var ink = CountdownInkSize(text, size, weight, metrics);
            if (ink.Width <= band.Width && ink.Height <= band.Height)
            {
                break;
            }
            size *= 0.96;
        }
        return Math.Max(MinimumFontSize, size);
    }
}
