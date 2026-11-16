namespace CanvasCountdown;

/// <summary>A point on the countdown tile, in pixels, with y growing downwards.</summary>
public readonly record struct TilePoint(double X, double Y);

/// <summary>A width and height on the countdown tile, in pixels.</summary>
public readonly record struct TileSize(double Width, double Height);

/// <summary>
/// A rectangle on the countdown tile, in pixels, with its origin at the top
/// left and y growing downwards — the flipped coordinates the Mac tile was
/// drawn in, and the ones Windows drawing uses.
/// </summary>
public readonly record struct TileRect(double X, double Y, double Width, double Height)
{
    public double MinX => X;

    public double MinY => Y;

    public double MaxX => X + Width;

    public double MaxY => Y + Height;

    public double MidX => X + (Width / 2);

    public double MidY => Y + (Height / 2);

    public TilePoint Origin => new(X, Y);

    public TileSize Size => new(Width, Height);

    /// <summary>The same rectangle shrunk by <paramref name="dx"/> on the left and right and <paramref name="dy"/> on the top and bottom.</summary>
    public TileRect InsetBy(double dx, double dy) => new(X + dx, Y + dy, Width - (2 * dx), Height - (2 * dy));

    public bool Contains(TileRect other, double tolerance = 0)
        => other.MinX >= MinX - tolerance
            && other.MinY >= MinY - tolerance
            && other.MaxX <= MaxX + tolerance
            && other.MaxY <= MaxY + tolerance;
}
