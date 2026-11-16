namespace CanvasCountdown;

/// <summary>
/// Everything needed to draw the countdown tile, decided once, so a renderer
/// only carries it out.
/// </summary>
/// <remarks>
/// <para>
/// One plan serves the tray icon, the taskbar badge and the settings preview,
/// so they can never drift apart — the Mac's renderer served the Dock tile,
/// the preview and the application icon the same way.
/// </para>
/// <para>
/// To draw it: clip to <see cref="TileRect"/> rounded by
/// <see cref="CornerRadius"/>; fill that rectangle with a vertical gradient
/// from <see cref="GradientTopColor"/> at its top edge to
/// <see cref="GradientBottomColor"/> at its bottom edge; draw
/// <see cref="Header"/> if there is one; then draw <see cref="Countdown"/>
/// with <see cref="CountdownShadow"/> beneath it. Text is drawn at a point,
/// never into a box: a box clips, and the tile must never lose part of a
/// glyph. All geometry is in pixels of a square bitmap
/// <see cref="PixelSize"/> wide, with y growing downwards.
/// </para>
/// </remarks>
public sealed record DockTileRenderPlan
{
    /// <summary>
    /// The largest tile, in logical pixels, drawn as digits only. The tray
    /// icon and the taskbar badge are 16 logical pixels at every display
    /// scale, so they are always compact; the settings preview is not.
    /// </summary>
    public const double CompactMaximumSize = 32;

    /// <summary>The header when none is given.</summary>
    public const string DefaultHeaderLabel = "DAYS";

    private DockTileRenderPlan()
    {
    }

    public double PixelSize { get; private init; }

    /// <summary>Digits only, as large as fit: the tray and badge sizes.</summary>
    public bool IsCompact { get; private init; }

    public TileRect TileRect { get; private init; }

    public double CornerRadius { get; private init; }

    /// <summary>
    /// The colour at the top edge of the tile. The Mac draws
    /// <see cref="DockColours.BackgroundBottom"/> here, and this keeps its look
    /// (see <see cref="Make"/>).
    /// </summary>
    public SrgbColor GradientTopColor { get; private init; }

    /// <summary>The colour at the bottom edge of the tile: the Mac's <see cref="DockColours.BackgroundTop"/>.</summary>
    public SrgbColor GradientBottomColor { get; private init; }

    /// <summary>The header band, or null in a compact plan.</summary>
    public TileRect? HeaderBand { get; private init; }

    /// <summary>The header, or null in a compact plan.</summary>
    public DockTileText? Header { get; private init; }

    public TileRect CountdownBand { get; private init; }

    /// <summary>The number, drawn as one run of text: never placed digit by digit.</summary>
    public DockTileText Countdown { get; private init; } = null!;

    public DockTileShadow CountdownShadow { get; private init; } = null!;

    /// <summary>The layout the plan was made from.</summary>
    public DockTileLayout Layout { get; private init; } = null!;

    /// <summary>
    /// The plan for one tile.
    /// </summary>
    /// <param name="appearance">The user's colours, size and weight.</param>
    /// <param name="daysRemaining">The countdown, or null for the dash shown when nothing is due.</param>
    /// <param name="label">The header. Empty or blank reads as <see cref="DefaultHeaderLabel"/>, as the Mac's tile did.</param>
    /// <param name="pixelSize">The width and height of the bitmap, in pixels.</param>
    /// <param name="scale">Pixels per logical pixel. Decides whether the tile is compact, so a 32-pixel tray icon on a 200% display is still treated as the 16-pixel icon it is.</param>
    /// <param name="metrics">Metrics of the face the renderer draws with. The reference metrics reproduce the Mac.</param>
    /// <remarks>
    /// The Mac painted its gradient with an angle that, in the flipped view it
    /// drew in, put the colour named "top" at the bottom of the tile. Every
    /// preset was chosen by looking at that tile, so the Windows tile draws it
    /// the same way rather than quietly changing how every theme looks.
    /// </remarks>
    public static DockTileRenderPlan Make(
        DockAppearance appearance,
        int? daysRemaining,
        string? label,
        double pixelSize,
        double scale = 1,
        IDockTileTextMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (!(pixelSize > 0) || double.IsInfinity(pixelSize))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelSize), pixelSize, "The tile needs a positive size.");
        }
        if (!(scale > 0) || double.IsInfinity(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be positive.");
        }
        metrics ??= DockTileTextMetrics.Reference;

        var text = DockTileLayout.Text(daysRemaining);
        var bounds = new TileRect(0, 0, pixelSize, pixelSize);
        var isCompact = pixelSize / scale <= CompactMaximumSize;
        var layout = isCompact
            ? DockTileLayout.MakeCompact(bounds, text, appearance.NumberWeight, metrics)
            : DockTileLayout.Make(bounds, text, appearance.NumberSize, appearance.NumberWeight, metrics);

        var countdown = new DockTileText
        {
            Text = text,
            Color = appearance.NumberColor,
            FontSize = layout.CountdownFontSize,
            FontWeight = appearance.NumberWeight.FontWeight(),
            Tracking = 0,
            Origin = layout.CountdownOrigin(text),
            Width = DockTileLayout.CountdownInkSize(text, layout.CountdownFontSize, appearance.NumberWeight, metrics).Width,
        };

        DockTileText? header = null;
        if (layout.HasHeader)
        {
            var headerText = string.IsNullOrWhiteSpace(label) ? DefaultHeaderLabel : label;
            var tracking = layout.LabelFontSize * 0.05;
            var measured = metrics.LabelSize(headerText, layout.LabelFontSize, tracking);
            header = new DockTileText
            {
                Text = headerText,
                Color = appearance.LabelColor,
                FontSize = layout.LabelFontSize,
                FontWeight = DockNumberWeight.Bold.FontWeight(),
                Tracking = tracking,
                Origin = layout.LabelOrigin(headerText, measured),
                Width = measured.Width,
            };
        }

        var tileHeight = layout.TileRect.Height;
        return new DockTileRenderPlan
        {
            PixelSize = pixelSize,
            IsCompact = isCompact,
            TileRect = layout.TileRect,
            CornerRadius = layout.CornerRadius,
            GradientTopColor = appearance.BackgroundBottomColor,
            GradientBottomColor = appearance.BackgroundTopColor,
            HeaderBand = layout.HasHeader ? layout.LabelRect : null,
            Header = header,
            CountdownBand = layout.CountdownRect,
            Countdown = countdown,
            CountdownShadow = new DockTileShadow
            {
                Color = SrgbColor.Black,
                Opacity = 0.28,
                BlurRadius = Math.Max(1, tileHeight * 0.015),
                OffsetX = 0,
                OffsetY = Math.Max(0.5, tileHeight * 0.008),
            },
            Layout = layout,
        };
    }
}

/// <summary>One run of text on the tile, and how to draw it.</summary>
public sealed record DockTileText
{
    public required string Text { get; init; }

    public required SrgbColor Color { get; init; }

    /// <summary>In pixels.</summary>
    public required double FontSize { get; init; }

    /// <summary>OpenType weight: 700 for bold, 800 for the Mac's heavy.</summary>
    public required int FontWeight { get; init; }

    /// <summary>
    /// The Mac drew both texts in its rounded system design. Windows has no
    /// rounded system face, so this asks for one where it exists and the
    /// regular system face otherwise.
    /// </summary>
    public bool PrefersRoundedDesign { get; init; } = true;

    /// <summary>
    /// Proportional figures, so a narrow "1" is not padded out to the width of
    /// a "0". Some Windows faces default to tabular figures; turn on the
    /// OpenType <c>pnum</c> feature for those.
    /// </summary>
    public bool UsesProportionalDigits { get; init; } = true;

    /// <summary>
    /// Extra space after every character, the last one included, in pixels.
    /// Zero for the number: any tracking there pulls the digits apart.
    /// </summary>
    public required double Tracking { get; init; }

    /// <summary>
    /// The top-left of the text's line box. Its baseline sits the face's
    /// ascender below this, as measured by the metrics the plan was made with.
    /// </summary>
    public required TilePoint Origin { get; init; }

    /// <summary>The run's advance width, tracking included.</summary>
    public required double Width { get; init; }
}

/// <summary>The shadow under the number.</summary>
public sealed record DockTileShadow
{
    public required SrgbColor Color { get; init; }

    public required double Opacity { get; init; }

    /// <summary>
    /// The blur radius as AppKit's <c>NSShadow</c> measures it, in pixels:
    /// roughly twice the standard deviation of a Gaussian blur.
    /// </summary>
    public required double BlurRadius { get; init; }

    public required double OffsetX { get; init; }

    /// <summary>Positive is downwards: the shadow falls below the digits.</summary>
    public required double OffsetY { get; init; }
}
