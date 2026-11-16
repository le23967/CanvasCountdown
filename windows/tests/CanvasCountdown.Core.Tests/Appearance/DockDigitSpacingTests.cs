using System.Globalization;

namespace CanvasCountdown.Tests.Appearance;

/// <summary>The countdown must read as one number, not as separately placed digits.</summary>
public sealed class DockDigitSpacingTests
{
    private static readonly string[] Values = ["11", "17", "21", "77", "100", "117", "157", "1024"];

    private static IDockTileTextMetrics Metrics => DockTileTextMetrics.Reference;

    [Fact]
    public void CountdownIsDrawnAsASingleTextRun()
    {
        // One run of text, one origin: there is no per-digit layout to
        // introduce gaps. If the drawing were ever split per character, the
        // measured width of the whole value would stop matching the sum of the
        // run rendered in one piece.
        foreach (var value in Values)
        {
            var layout = DockTileLayout.Make(new TileRect(0, 0, 128, 128), value);
            var wholeRun = Metrics.CountdownWidth(value, layout.CountdownFontSize, DockNumberWeight.Heavy);
            var perCharacterSum = value.Sum(character =>
                Metrics.CountdownWidth(character.ToString(), layout.CountdownFontSize, DockNumberWeight.Heavy));

            Assert.Equal(perCharacterSum, wholeRun, Math.Max(0.5, wholeRun * 0.01));

            var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, int.Parse(value, CultureInfo.InvariantCulture), "DAYS", 128);
            Assert.Equal(value, plan.Countdown.Text);
            Assert.Equal(wholeRun, plan.Countdown.Width, 9);
        }
    }

    [Fact]
    public void NoPositiveTrackingIsAppliedToTheNumber()
    {
        foreach (var value in Values)
        {
            var layout = DockTileLayout.Make(new TileRect(0, 0, 128, 128), value);
            var plain = Metrics.CountdownWidth(value, layout.CountdownFontSize, DockNumberWeight.Heavy);
            var measured = DockTileLayout.CountdownInkSize(value, layout.CountdownFontSize).Width;

            Assert.Equal(plain, measured, 0.001);
            Assert.Equal(0, DockTileRenderPlan.Make(DockAppearance.Defaults, int.Parse(value, CultureInfo.InvariantCulture), "DAYS", 128).Countdown.Tracking);
        }
    }

    [Fact]
    public void DigitsUseProportionalWidthsSoNarrowOnesDoNotFloat()
    {
        var one = Metrics.CountdownWidth("1", 100, DockNumberWeight.Heavy);
        var zero = Metrics.CountdownWidth("0", 100, DockNumberWeight.Heavy);

        Assert.True(
            one < zero * 0.95,
            "A monospaced digit set pads \"1\" to the width of \"0\", which is what left a gap in values like 17");
        Assert.True(
            DockTileRenderPlan.Make(DockAppearance.Defaults, 17, "DAYS", 128).Countdown.UsesProportionalDigits,
            "A Windows face that defaults to tabular figures has to be told");
    }

    [Fact]
    public void TwoDigitValuesContainingOneAreCompact()
    {
        foreach (var value in new[] { "11", "17", "71" })
        {
            var width = Metrics.CountdownWidth(value, 100, DockNumberWeight.Heavy);
            var doubleZero = Metrics.CountdownWidth("00", 100, DockNumberWeight.Heavy);

            Assert.True(width < doubleZero, $"{value} should be narrower than \"00\" now that digits are proportional");
        }
    }

    [Fact]
    public void EveryTestedValueStaysInsideItsBandAtEverySize()
    {
        foreach (var size in Enum.GetValues<DockNumberSize>())
        {
            foreach (var value in Values)
            {
                foreach (var tile in new[] { 32.0, 64.0, 128.0 })
                {
                    var layout = DockTileLayout.Make(new TileRect(0, 0, tile, tile), value, size);
                    var ink = DockTileLayout.CountdownInkSize(value, layout.CountdownFontSize);

                    Assert.True(ink.Width <= layout.CountdownRect.Width + 0.5, $"{value} at {size.Title()}/{tile}");
                    Assert.True(ink.Height <= layout.CountdownRect.Height + 0.5, $"{value} at {size.Title()}/{tile}");
                }
            }
        }
    }

    /// <summary>
    /// What must not happen is the drawn value occupying more width than one
    /// text run needs, which is the signature of per-digit placement or added
    /// tracking. The Mac checked the rendered pixels; the plan is what a
    /// Windows renderer draws from, so the run it describes is checked here.
    /// </summary>
    [Fact]
    public void RenderedSeventeenOccupiesExactlyOneTextRunOfWidth()
    {
        foreach (var value in new[] { "17", "11", "117", "1024" })
        {
            var plan = DockTileRenderPlan.Make(DockAppearance.Defaults, int.Parse(value, CultureInfo.InvariantCulture), "DAYS", 256);
            var runWidth = DockTileLayout.CountdownInkSize(value, plan.Countdown.FontSize).Width;
            var drawnWidth = plan.Countdown.Width + (plan.Countdown.Tracking * value.Length);

            Assert.True(drawnWidth <= runWidth * 1.02, $"{value} is drawn wider than one text run: digits are being spaced apart");
            Assert.True(drawnWidth > runWidth * 0.7, $"{value} does not fill its run, which suggests it is not one piece of text");
            Assert.Equal(plan.CountdownBand.MidX, plan.Countdown.Origin.X + (drawnWidth / 2), 6);
        }
    }
}
