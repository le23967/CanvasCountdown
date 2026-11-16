namespace CanvasCountdown.Tests.Appearance;

/// <summary>
/// The port of the layout arithmetic, checked against what the Mac's own code
/// answered rather than against expectations written here.
/// </summary>
/// <remarks>
/// The reference metrics are the Mac font measured at 100 points. Below about
/// 80 points that font tracks text slightly wider than its 100-point shape (by
/// 0.7% at 60 points, 2% at 30), which the reference does not model. So the
/// sizes come out exactly the Mac's wherever the fitting happens at display
/// sizes, and the horizontal origin of a small final number can sit a fraction
/// of a pixel off.
/// </remarks>
public sealed class DockTileLayoutMacReferenceTests
{
    public static TheoryData<int> DisplaySizeRows() => Rows(tile => tile >= 128);

    public static TheoryData<int> PreviewSizeRows() => Rows(tile => tile < 128);

    [Theory]
    [MemberData(nameof(DisplaySizeRows))]
    public void TheReferenceMetricsReproduceTheMacLayout(int index)
    {
        var row = MacTileLayoutReference.Rows[index];
        var layout = DockTileLayout.Make(new TileRect(0, 0, row.Tile, row.Tile), row.Value, row.Size, row.Weight);
        var origin = layout.CountdownOrigin(row.Value);

        Assert.Equal(row.SafeFontSize, DockTileLayout.MaximumSafeFontSize(row.Value, layout.CountdownRect, row.Weight), 6);
        Assert.Equal(row.CountdownFontSize, layout.CountdownFontSize, 6);
        Assert.Equal(row.LabelFontSize, layout.LabelFontSize, 6);
        Assert.Equal(row.OriginY, origin.Y, 6);
        Assert.InRange(origin.X - row.OriginX, -0.01 * row.Tile, 0.01 * row.Tile);
    }

    /// <summary>
    /// At the settings preview's 76 points the Mac's wider small-size tracking
    /// can tip a value over the edge of its band, and the Mac then takes one
    /// more fitting step. Never more than one, and never the other way.
    /// </summary>
    [Theory]
    [MemberData(nameof(PreviewSizeRows))]
    public void AtThePreviewSizeTheMacIsAtMostOneFittingStepSmaller(int index)
    {
        var row = MacTileLayoutReference.Rows[index];
        var layout = DockTileLayout.Make(new TileRect(0, 0, row.Tile, row.Tile), row.Value, row.Size, row.Weight);
        var safe = DockTileLayout.MaximumSafeFontSize(row.Value, layout.CountdownRect, row.Weight);

        var steps = Math.Log(safe / row.SafeFontSize) / Math.Log(1 / 0.96);
        Assert.True(
            Math.Abs(steps) < 1e-9 || Math.Abs(steps - 1) < 1e-9,
            $"{row.Value} {row.Size} {row.Weight}: {steps} steps from the Mac");
        if (Math.Abs(steps) < 1e-9)
        {
            Assert.Equal(row.CountdownFontSize, layout.CountdownFontSize, 6);
            Assert.Equal(row.LabelFontSize, layout.LabelFontSize, 6);
            Assert.Equal(row.OriginY, layout.CountdownOrigin(row.Value).Y, 6);
        }
        Assert.InRange(layout.CountdownOrigin(row.Value).X - row.OriginX, -0.015 * row.Tile, 0.015 * row.Tile);
    }

    [Fact]
    public void TheBandsMatchTheMacAtTheDockSize()
    {
        var layout = DockTileLayout.Make(new TileRect(0, 0, 128, 128), "21");

        Assert.Equal(new TileRect(3.968, 3.968, 120.064, 120.064), Rounded(layout.TileRect));
        Assert.Equal(21.61152, layout.CornerRadius, 9);
        Assert.Equal(new TileRect(8.77056, 9.9712, 110.45888, 20.41088), Rounded(layout.LabelRect));
        Assert.Equal(new TileRect(8.77056, 32.78336, 110.45888, 79.24224), Rounded(layout.CountdownRect));
    }

    private static TheoryData<int> Rows(Func<int, bool> include)
    {
        var data = new TheoryData<int>();
        for (var index = 0; index < MacTileLayoutReference.Rows.Length; index++)
        {
            if (include(MacTileLayoutReference.Rows[index].Tile))
            {
                data.Add(index);
            }
        }
        return data;
    }

    private static TileRect Rounded(TileRect rect)
        => new(Math.Round(rect.X, 9), Math.Round(rect.Y, 9), Math.Round(rect.Width, 9), Math.Round(rect.Height, 9));
}
