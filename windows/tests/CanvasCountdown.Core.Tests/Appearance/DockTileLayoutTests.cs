namespace CanvasCountdown.Tests.Appearance;

/// <summary>
/// The countdown tile has to stay readable at every size it is drawn at, with
/// the countdown numeral as the dominant element and nothing clipped.
/// </summary>
public sealed class DockTileLayoutTests
{
    private static readonly double[] TileSizes = [32, 48, 64, 128, 256, 1_024];
    private static readonly string[] Values = ["0", "7", "21", "157", "1024", DockTileLayout.Placeholder];

    [Fact]
    public void LabelOccupiesTheUpperBandOnly()
    {
        foreach (var size in TileSizes)
        {
            var layout = Layout(size, "7");
            var tile = layout.TileRect;
            var topFraction = (layout.LabelRect.MinY - tile.MinY) / tile.Height;
            var bottomFraction = (layout.LabelRect.MaxY - tile.MinY) / tile.Height;

            Assert.True(topFraction > 0.02, $"size {size}");
            Assert.True(bottomFraction <= 0.24, $"The header must stay in the top fifth at size {size}");
        }
    }

    [Fact]
    public void CountdownOccupiesTheCentralSixtyToSeventyPercent()
    {
        foreach (var size in TileSizes)
        {
            var layout = Layout(size, "7");
            var tile = layout.TileRect;
            var heightFraction = layout.CountdownRect.Height / tile.Height;

            Assert.InRange(heightFraction, 0.60, 0.70);
            Assert.True(
                layout.CountdownRect.MinY > layout.LabelRect.MaxY - 0.001,
                $"The number must sit below the header at size {size}");
            Assert.True(
                layout.CountdownRect.MaxY <= tile.MaxY + 0.001,
                $"The number band must stay inside the tile at size {size}");
        }
    }

    [Fact]
    public void NoValueIsClippedAtAnySize()
    {
        var metrics = DockTileTextMetrics.Reference;
        foreach (var size in TileSizes)
        {
            foreach (var value in Values)
            {
                var layout = Layout(size, value);
                var ink = DockTileLayout.CountdownInkSize(value, layout.CountdownFontSize);

                Assert.True(ink.Width <= layout.CountdownRect.Width + 0.5, $"\"{value}\" overflows horizontally at size {size}");
                Assert.True(ink.Height <= layout.CountdownRect.Height + 0.5, $"\"{value}\" overflows vertically at size {size}");

                var origin = layout.CountdownOrigin(value);
                var ascender = metrics.CountdownAscender(layout.CountdownFontSize, DockNumberWeight.Heavy);
                var capHeight = metrics.CountdownCapHeight(layout.CountdownFontSize, DockNumberWeight.Heavy);
                var capTop = origin.Y + ascender - capHeight;
                Assert.True(capTop >= layout.CountdownRect.MinY - 0.5, $"\"{value}\" is drawn above its band at size {size}");
                Assert.True(origin.Y + ascender <= layout.CountdownRect.MaxY + 0.5, $"\"{value}\" is drawn below its band at size {size}");
                Assert.True(origin.X >= layout.CountdownRect.MinX - 0.5, $"\"{value}\" starts left of its band at size {size}");
            }
        }
    }

    [Fact]
    public void CountdownIsTheDominantElement()
    {
        foreach (var size in TileSizes)
        {
            foreach (var value in Values)
            {
                var layout = Layout(size, value);

                Assert.True(
                    layout.CountdownFontSize > layout.LabelFontSize * 2.5,
                    $"\"{value}\" is not dominant at size {size}");
            }
        }
    }

    [Fact]
    public void ShorterValuesAreDrawnLarger()
    {
        var oneDigit = Layout(128, "7");
        var twoDigits = Layout(128, "21");
        var threeDigits = Layout(128, "157");
        var fourCharacters = Layout(128, "1024");

        Assert.True(oneDigit.CountdownFontSize > twoDigits.CountdownFontSize);
        Assert.True(twoDigits.CountdownFontSize > threeDigits.CountdownFontSize);
        Assert.True(threeDigits.CountdownFontSize > fourCharacters.CountdownFontSize);
    }

    [Fact]
    public void FontSizesScaleWithTheTile()
    {
        var previous = 0.0;
        foreach (var size in TileSizes)
        {
            var current = Layout(size, "21").CountdownFontSize;
            Assert.True(current > previous, $"size {size} did not grow");
            previous = current;
        }
    }

    [Fact]
    public void ExtraLargeUsesMostOfTheAvailableSpace()
    {
        // The original implementation drew two digits at a fixed 0.50 of the
        // tile height. Extra Large is the setting that now claims the full safe
        // size; the smaller settings deliberately sit below it.
        var layout = DockTileLayout.Make(new TileRect(0, 0, 128, 128), "21", DockNumberSize.ExtraLarge);

        Assert.True(
            layout.CountdownFontSize > layout.TileRect.Height * 0.60,
            "Two digits at Extra Large should be far larger than the old 0.50 scale");
        Assert.Equal(DockTileLayout.MaximumSafeFontSize("21", layout.CountdownRect), layout.CountdownFontSize, 0.001);
    }

    [Fact]
    public void PlaceholderStillFitsAndStaysSmallerThanADigit()
    {
        var placeholder = Layout(128, DockTileLayout.Placeholder);
        var digit = Layout(128, "7");
        var measured = DockTileLayout.CountdownInkSize(DockTileLayout.Placeholder, placeholder.CountdownFontSize);

        Assert.True(placeholder.CountdownFontSize < digit.CountdownFontSize);
        Assert.True(measured.Width <= placeholder.CountdownRect.Width + 0.5);
    }

    [Fact]
    public void DaysRemainingTextMapping()
    {
        Assert.Equal("0", DockTileLayout.Text(0));
        Assert.Equal("157", DockTileLayout.Text(157));
        Assert.Equal(DockTileLayout.Placeholder, DockTileLayout.Text(null));
    }

    /// <summary>
    /// The Mac rendered the live tile and counted bright pixels in each band.
    /// Here the plan the renderer will execute is checked instead: the number's
    /// ink lies inside its band, and it outweighs the header.
    /// </summary>
    [Fact]
    public void RenderedTileDrawsInkInsideTheNumberBand()
    {
        var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, 21, "DAYS", 128);
        var metrics = DockTileTextMetrics.Reference;
        var capHeight = metrics.CountdownCapHeight(plan.Countdown.FontSize, DockNumberWeight.Heavy);
        var baseline = plan.Countdown.Origin.Y + metrics.CountdownAscender(plan.Countdown.FontSize, DockNumberWeight.Heavy);
        var numberInk = new TileRect(plan.Countdown.Origin.X, baseline - capHeight, plan.Countdown.Width, capHeight);
        var header = Assert.IsType<DockTileText>(plan.Header);
        var headerInk = header.Width * metrics.LabelSize(header.Text, header.FontSize, 0).Height;

        Assert.True(plan.CountdownBand.Contains(numberInk, tolerance: 0.5), "The countdown band should carry the number's ink");
        Assert.True(
            numberInk.Width * numberInk.Height / (plan.CountdownBand.Width * plan.CountdownBand.Height) > 0.10,
            "The countdown band should carry substantial ink");
        Assert.True(numberInk.Width * numberInk.Height > headerInk, "The number must read more strongly than the header");
    }

    private static DockTileLayout Layout(double size, string text)
        => DockTileLayout.Make(new TileRect(0, 0, size, size), text);
}
