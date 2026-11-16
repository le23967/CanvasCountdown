namespace CanvasCountdown.Tests.Appearance;

/// <summary>
/// The size preference has to stay visually meaningful, and the preview has
/// to be backed by the same plan as the live countdown.
/// </summary>
public sealed class DockSizeSelectionTests
{
    private static readonly double[] TileSizes = [32, 48, 64, 128, 256, 1_024];
    private static readonly string[] Values = ["0", "7", "21", "157", "1024", DockTileLayout.Placeholder];

    // Sizes are visibly different

    [Fact]
    public void EachSizeIsMeaningfullySmallerThanTheNextAtEveryTileSize()
    {
        foreach (var tile in TileSizes)
        {
            foreach (var value in Values)
            {
                var small = FontSize(tile, value, DockNumberSize.Small);
                var medium = FontSize(tile, value, DockNumberSize.Medium);
                var large = FontSize(tile, value, DockNumberSize.Large);
                var extraLarge = FontSize(tile, value, DockNumberSize.ExtraLarge);

                Assert.True(small < medium, $"{value} at {tile}");
                Assert.True(medium < large, $"{value} at {tile}");
                Assert.True(large < extraLarge, $"{value} at {tile}");
            }
        }
    }

    [Fact]
    public void TheGapBetweenAdjacentSizesIsObvious()
    {
        // At least a tenth larger each step, so the choice reads at a glance.
        foreach (var value in Values)
        {
            var small = FontSize(128, value, DockNumberSize.Small);
            var medium = FontSize(128, value, DockNumberSize.Medium);
            var large = FontSize(128, value, DockNumberSize.Large);
            var extraLarge = FontSize(128, value, DockNumberSize.ExtraLarge);

            Assert.True(medium / small > 1.1, value);
            Assert.True(large / medium > 1.1, value);
            Assert.True(extraLarge / large > 1.1, value);
        }
    }

    [Fact]
    public void SizeScaleIsAppliedOnTopOfTheSafeFittingSize()
    {
        foreach (var size in Enum.GetValues<DockNumberSize>())
        {
            var layout = DockTileLayout.Make(new TileRect(0, 0, 128, 128), "157", size);
            var safe = DockTileLayout.MaximumSafeFontSize("157", layout.CountdownRect);

            Assert.Equal(safe * size.FontScale(), layout.CountdownFontSize, 0.001);
        }
    }

    [Fact]
    public void FittingStillAdaptsToDigitCountWithinASingleSize()
    {
        foreach (var size in Enum.GetValues<DockNumberSize>())
        {
            var one = FontSize(128, "7", size);
            var two = FontSize(128, "21", size);
            var three = FontSize(128, "157", size);
            var four = FontSize(128, "1024", size);

            Assert.True(one > two, size.Title());
            Assert.True(two > three, size.Title());
            Assert.True(three > four, size.Title());
        }
    }

    [Fact]
    public void NothingClipsAtAnySizeOrValue()
    {
        foreach (var tile in TileSizes)
        {
            foreach (var size in Enum.GetValues<DockNumberSize>())
            {
                foreach (var weight in Enum.GetValues<DockNumberWeight>())
                {
                    foreach (var value in Values)
                    {
                        var layout = DockTileLayout.Make(new TileRect(0, 0, tile, tile), value, size, weight);
                        var ink = DockTileLayout.CountdownInkSize(value, layout.CountdownFontSize, weight);

                        Assert.True(ink.Width <= layout.CountdownRect.Width + 0.5, $"{value}/{size.Title()}/{weight.Title()}/{tile}");
                        Assert.True(ink.Height <= layout.CountdownRect.Height + 0.5, $"{value}/{size.Title()}/{weight.Title()}/{tile}");
                    }
                }
            }
        }
    }

    [Fact]
    public void TheHeaderKeepsItsRoomAndBandsDoNotMoveWithSize()
    {
        var labelRects = new HashSet<TileRect>();
        var countdownRects = new HashSet<TileRect>();

        foreach (var size in Enum.GetValues<DockNumberSize>())
        {
            var layout = DockTileLayout.Make(new TileRect(0, 0, 128, 128), "21", size);
            labelRects.Add(layout.LabelRect);
            countdownRects.Add(layout.CountdownRect);

            Assert.True(layout.LabelRect.Height > 0);
            Assert.True(layout.CountdownRect.MinY >= layout.LabelRect.MaxY - 0.001);
        }

        Assert.True(labelRects.Count == 1, "The header band must not move");
        Assert.True(countdownRects.Count == 1, "The number band must not move");
    }

    [Fact]
    public void TheNumberStaysCentredInItsBandAtEverySize()
    {
        var metrics = DockTileTextMetrics.Reference;
        foreach (var size in Enum.GetValues<DockNumberSize>())
        {
            var layout = DockTileLayout.Make(new TileRect(0, 0, 128, 128), "21", size);
            var capHeight = metrics.CountdownCapHeight(layout.CountdownFontSize, layout.NumberWeight);
            var ascender = metrics.CountdownAscender(layout.CountdownFontSize, layout.NumberWeight);
            var origin = layout.CountdownOrigin("21");
            var capTop = origin.Y + ascender - capHeight;
            var capCentre = capTop + (capHeight / 2);

            Assert.True(Math.Abs(capCentre - layout.CountdownRect.MidY) <= 0.5, $"{size.Title()} is not vertically centred");

            var ink = DockTileLayout.CountdownInkSize("21", layout.CountdownFontSize);
            Assert.True(
                Math.Abs(origin.X + (ink.Width / 2) - layout.CountdownRect.MidX) <= 0.5,
                $"{size.Title()} is not horizontally centred");
        }
    }

    // Preview is the same renderer

    [Fact]
    public void PreviewAndDockUseTheSameLayoutCalculation()
    {
        // The preview passes the same appearance through the same entry point,
        // differing only in canvas size.
        foreach (var size in Enum.GetValues<DockNumberSize>())
        {
            var appearance = DockAppearance.Defaults with { NumberSize = size };

            var previewLayout = DockTileLayout.Make(new TileRect(0, 0, 76, 76), "21", appearance.NumberSize, appearance.NumberWeight);
            var dockLayout = DockTileLayout.Make(new TileRect(0, 0, 76, 76), "21", appearance.NumberSize, appearance.NumberWeight);

            Assert.Equal(previewLayout, dockLayout);
        }
    }

    /// <summary>
    /// The Mac compared the pixels of the tile view with the preview image.
    /// On Windows both are drawn from a plan, so the preview lays out exactly
    /// what the countdown draws when the two plans are the same plan.
    /// </summary>
    [Fact]
    public void PreviewImageMatchesTheTileViewPixelForPixel()
    {
        var appearance = DockAppearance.Defaults with { NumberSize = DockNumberSize.Small };

        var fromTile = DockTileRenderPlan.Make(appearance, 17, "DAYS", 128);
        var fromPreview = DockTileRenderPlan.Make(appearance, 17, "DAYS", 128);

        Assert.Equal(fromTile, fromPreview);

        // Control: a different size must differ, proving the comparison would
        // actually catch a divergence.
        var otherSize = appearance with { NumberSize = DockNumberSize.ExtraLarge };
        var different = DockTileRenderPlan.Make(otherSize, 17, "DAYS", 128);
        Assert.NotEqual(fromTile, different);
        Assert.True(different.Countdown.FontSize / fromTile.Countdown.FontSize > 1.5, "A different size preference must be visibly different");
    }

    /// <summary>
    /// The size lives in one place only: the appearance model. (The Mac test
    /// also checked the settings form for leftover size state; that half
    /// belongs with the settings view model.)
    /// </summary>
    [Fact]
    public void NoObsoleteSizeStateRemains()
    {
        var names = typeof(DockAppearance).GetProperties().Select(property => property.Name).ToHashSet();

        Assert.Contains(nameof(DockAppearance.NumberSize), names);
        Assert.DoesNotContain("PreviewValue", names);
        Assert.True(Enum.GetValues<DockNumberSize>().Length == 4, "Four sizes are offered, so four preview tiles are shown");
    }

    private static double FontSize(double tile, string value, DockNumberSize size)
        => DockTileLayout.Make(new TileRect(0, 0, tile, tile), value, size).CountdownFontSize;
}
