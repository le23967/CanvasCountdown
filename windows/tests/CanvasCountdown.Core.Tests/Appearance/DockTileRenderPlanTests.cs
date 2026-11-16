namespace CanvasCountdown.Tests.Appearance;

/// <summary>
/// The plan a Windows renderer executes: the Mac tile's decisions, and an
/// answer for the tray icon and the taskbar badge, which are 16 logical pixels.
/// </summary>
public sealed class DockTileRenderPlanTests
{
    private static readonly int[] CompactSizes = [16, 20, 24, 32];
    private static readonly int?[] Values = [0, 7, 21, 157, 1024, null];

    [Fact]
    public void TraySizesDrawDigitsOnly()
    {
        foreach (var size in CompactSizes)
        {
            var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, 21, "DAYS", size);

            Assert.True(plan.IsCompact, $"{size}");
            Assert.Null(plan.Header);
            Assert.Null(plan.HeaderBand);
            Assert.Equal("21", plan.Countdown.Text);
        }
    }

    [Fact]
    public void TraySizesDrawTheNumberAsLargeAsFits()
    {
        var metrics = DockTileTextMetrics.Reference;
        foreach (var size in CompactSizes)
        {
            foreach (var days in Values)
            {
                foreach (var weight in Enum.GetValues<DockNumberWeight>())
                {
                    var appearance = DockAppearance.Defaults with { NumberWeight = weight };
                    var plan = DockTileRenderPlan.Make(appearance, days, "DAYS", size);
                    var text = plan.Countdown.Text;
                    var band = plan.CountdownBand;
                    var ink = DockTileLayout.CountdownInkSize(text, plan.Countdown.FontSize, weight);
                    var label = $"{text} at {size} in {weight.Title()}";

                    Assert.Equal(DockTileLayout.MaximumSafeFontSize(text, band, weight), plan.Countdown.FontSize, 9);
                    Assert.True(ink.Width <= band.Width + 1e-9, $"{label} overflows");
                    Assert.True(ink.Height <= band.Height + 1e-9, $"{label} overflows");
                    Assert.True(plan.TileRect.Contains(band), $"{label}: the band must stay inside the tile");

                    // As large as fits: one more fitting step would not fit, or
                    // the digits already fill their share of the height.
                    var nextSize = plan.Countdown.FontSize / 0.96;
                    var next = DockTileLayout.CountdownInkSize(text, nextSize, weight);
                    var capFill = text == DockTileLayout.Placeholder
                        ? DockTileLayout.Proportion.PlaceholderCapFill
                        : DockTileLayout.Proportion.DigitCapFill;
                    var fillsHeight = Math.Abs(metrics.CountdownCapHeight(plan.Countdown.FontSize, weight) - (band.Height * capFill)) < 1e-9;
                    Assert.True(fillsHeight || next.Width > band.Width, $"{label} could be larger");
                }
            }
        }
    }

    /// <summary>
    /// The header's band goes to the number, so a value limited by height
    /// grows; one limited by width has the same width to work with as before.
    /// </summary>
    [Fact]
    public void TraySizesGiveTheNumberMoreRoomThanTheFullTileWould()
    {
        foreach (var size in CompactSizes)
        {
            foreach (var days in Values)
            {
                var text = DockTileLayout.Text(days);
                var compact = DockTileRenderPlan.Make(DockAppearance.Defaults with { NumberSize = DockNumberSize.ExtraLarge }, days, "DAYS", size);
                var full = DockTileLayout.Make(new TileRect(0, 0, size, size), text, DockNumberSize.ExtraLarge);

                Assert.True(compact.Countdown.FontSize >= full.CountdownFontSize, $"{text} at {size}");
                if (text.Length == 1)
                {
                    Assert.True(compact.Countdown.FontSize > full.CountdownFontSize * 1.3, $"{text} at {size}");
                }
            }
        }
    }

    /// <summary>
    /// At 16 pixels every pixel the number gives up costs legibility, so the
    /// size preference does not shrink it there. The weight still applies.
    /// </summary>
    [Fact]
    public void TheSizePreferenceDoesNotShrinkTheTrayIcon()
    {
        var sizes = Enum.GetValues<DockNumberSize>()
            .Select(size => DockTileRenderPlan.Make(DockAppearance.Defaults with { NumberSize = size }, 21, "DAYS", 16).Countdown.FontSize)
            .Distinct()
            .ToList();
        var bold = DockTileRenderPlan.Make(DockAppearance.Defaults with { NumberWeight = DockNumberWeight.Bold }, 21, "DAYS", 16);

        Assert.Single(sizes);
        Assert.Equal(700, bold.Countdown.FontWeight);
    }

    [Theory]
    [InlineData(16, 1, true)]
    [InlineData(32, 1, true)]
    [InlineData(33, 1, false)]
    [InlineData(48, 1, false)]
    [InlineData(76, 1, false)]
    [InlineData(32, 2, true)]
    [InlineData(48, 3, true)]
    [InlineData(64, 2, true)]
    [InlineData(152, 2, false)]
    public void WhetherTheTileIsCompactFollowsItsLogicalSize(double pixels, double scale, bool isCompact)
    {
        var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, 7, "DAYS", pixels, scale);

        Assert.Equal(isCompact, plan.IsCompact);
        Assert.Equal(isCompact, plan.Header is null);
        Assert.Equal(pixels, plan.PixelSize);
    }

    [Theory]
    [InlineData("", "DAYS")]
    [InlineData("  \n", "DAYS")]
    [InlineData(null, "DAYS")]
    [InlineData("DAYS", "DAYS")]
    [InlineData("倒数日", "倒数日")]
    public void ABlankHeaderReadsDays(string? label, string shown)
    {
        var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, 3, label, 128);

        Assert.Equal(shown, plan.Header?.Text);
    }

    [Fact]
    public void TheHeaderIsCentredInItsBandAndSpacedOut()
    {
        var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, 21, "DAYS", 128);
        var header = Assert.IsType<DockTileText>(plan.Header);
        var band = Assert.IsType<TileRect>(plan.HeaderBand);
        var measured = DockTileTextMetrics.Reference.LabelSize("DAYS", header.FontSize, header.Tracking);

        Assert.Equal(plan.Layout.LabelFontSize, header.FontSize);
        Assert.Equal(header.FontSize * 0.05, header.Tracking, 12);
        Assert.Equal(band.MidX - (measured.Width / 2), header.Origin.X, 9);
        Assert.Equal(band.MidY - (measured.Height / 2), header.Origin.Y, 9);
        Assert.Equal(700, header.FontWeight);
        Assert.Equal(DockAppearance.Defaults.LabelColor, header.Color);
        Assert.True(header.PrefersRoundedDesign);
    }

    /// <summary>
    /// The Mac drew its gradient at an angle that, in the flipped view it drew
    /// in, put the colour named "top" at the bottom. Every preset was chosen
    /// by looking at that tile, so the plan keeps its look: Default Blue is
    /// lighter at the top.
    /// </summary>
    [Fact]
    public void TheGradientRunsTheWayTheMacDrewIt()
    {
        var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, 21, "DAYS", 128);

        Assert.Equal("#3D7AD1", plan.GradientTopColor.HexString);
        Assert.Equal("#21448C", plan.GradientBottomColor.HexString);
        Assert.True(plan.GradientTopColor.RelativeLuminance > plan.GradientBottomColor.RelativeLuminance);
    }

    [Fact]
    public void TheShadowFallsBelowTheDigits()
    {
        var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, 21, "DAYS", 256);
        var tileHeight = plan.TileRect.Height;

        Assert.Equal(SrgbColor.Black, plan.CountdownShadow.Color);
        Assert.Equal(0.28, plan.CountdownShadow.Opacity);
        Assert.Equal(tileHeight * 0.015, plan.CountdownShadow.BlurRadius, 12);
        Assert.Equal(0, plan.CountdownShadow.OffsetX);
        Assert.Equal(tileHeight * 0.008, plan.CountdownShadow.OffsetY, 12);
        Assert.True(plan.CountdownShadow.OffsetY > 0, "Positive is down");

        var tiny = DockTileRenderPlan.Make(DockAppearance.Defaults, 21, "DAYS", 16);
        Assert.Equal(1, tiny.CountdownShadow.BlurRadius);
        Assert.Equal(0.5, tiny.CountdownShadow.OffsetY);
    }

    [Fact]
    public void TheTileShapeIsTheMacs()
    {
        var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, 21, "DAYS", 128);

        Assert.Equal(new TileRect(3.968, 3.968, 120.064, 120.064), plan.TileRect);
        Assert.Equal(21.61152, plan.CornerRadius, 9);
        Assert.Equal(plan.Layout.CountdownRect, plan.CountdownBand);
        Assert.Equal(plan.Layout.CountdownOrigin("21"), plan.Countdown.Origin);
    }

    [Fact]
    public void WeightsUseTheOpenTypeScale()
    {
        Assert.Equal(700, DockNumberWeight.Bold.FontWeight());
        Assert.Equal(800, DockNumberWeight.Heavy.FontWeight());
        Assert.Equal(800, DockTileRenderPlan.Make(DockAppearance.Defaults, 21, "DAYS", 128).Countdown.FontWeight);
    }

    /// <summary>
    /// The renderer measures with the face it draws: a face with wider digits
    /// gets a smaller number, and it still fits by its own measure.
    /// </summary>
    [Fact]
    public void ARendererMeasuresWithItsOwnFace()
    {
        var wide = new WideDigitMetrics();
        foreach (var days in Values)
        {
            var reference = DockTileRenderPlan.Make(DockAppearance.Defaults, days, "DAYS", 128);
            var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, days, "DAYS", 128, metrics: wide);
            var text = plan.Countdown.Text;

            Assert.True(wide.CountdownWidth(text, plan.Countdown.FontSize, DockNumberWeight.Heavy) <= plan.CountdownBand.Width + 1e-9, text);
            Assert.True(plan.Countdown.FontSize <= reference.Countdown.FontSize + 1e-9, text);
            Assert.Same(wide, plan.Layout.Metrics);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-16, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(16, 0)]
    [InlineData(16, double.NaN)]
    public void ATileNeedsARealSize(double pixels, double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DockTileRenderPlan.Make(DockAppearance.Defaults, 1, "DAYS", pixels, scale));
    }

    [Fact]
    public void TheCompactLayoutKeepsTheTileShape()
    {
        var compact = DockTileLayout.MakeCompact(new TileRect(0, 0, 16, 16), "7");
        var full = DockTileLayout.Make(new TileRect(0, 0, 16, 16), "7");

        Assert.Equal(full.TileRect, compact.TileRect);
        Assert.Equal(full.CornerRadius, compact.CornerRadius);
        Assert.False(compact.HasHeader);
        Assert.True(full.HasHeader);
        Assert.Equal(0, compact.LabelFontSize);
    }

    private sealed class WideDigitMetrics : IDockTileTextMetrics
    {
        public double CountdownWidth(string text, double fontSize, DockNumberWeight weight) => text.Length * 0.75 * fontSize;

        public double CountdownCapHeight(double fontSize, DockNumberWeight weight) => 0.72 * fontSize;

        public double CountdownAscender(double fontSize, DockNumberWeight weight) => 0.95 * fontSize;

        public TileSize LabelSize(string text, double fontSize, double tracking) => new((text.Length * ((0.6 * fontSize) + tracking)), 1.3 * fontSize);
    }
}
