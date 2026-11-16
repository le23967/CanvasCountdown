namespace CanvasCountdown;

/// <summary>
/// A rectangle on a screenshot, measured in fractions of the image: 0 to 1
/// across and 0 to 1 down, with the origin at the <b>top left</b>.
/// </summary>
/// <remarks>
/// <para>
/// Fractions rather than pixels, so browser zoom, display scaling and the size
/// of the capture do not change how rows are grouped. Top left because that is
/// how a page is read and how Windows.Media.Ocr reports its rectangles. Apple
/// Vision, which the macOS app uses, reports normalised boxes with the origin
/// at the bottom left.
/// </para>
/// <para>
/// Each recogniser converts into this convention once, at its boundary —
/// <see cref="FromPixels"/> for pixel rectangles, <see cref="FromBottomLeftOrigin"/>
/// for bottom-left boxes — so the parser never knows which one it is reading.
/// An inverted comparison there would silently sort rows upside down.
/// </para>
/// <para>
/// Stored standardised, as Core Graphics reports a rectangle: a negative width
/// or height is folded into the origin, so a careless conversion cannot flip a
/// comparison either.
/// </para>
/// </remarks>
public readonly record struct NormalisedRect
{
    public NormalisedRect(double x, double y, double width, double height)
    {
        X = width < 0 ? x + width : x;
        Y = height < 0 ? y + height : y;
        Width = Math.Abs(width);
        Height = Math.Abs(height);
    }

    public static NormalisedRect Zero => default;

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public double MinX => X;

    public double MinY => Y;

    public double MaxX => X + Width;

    public double MaxY => Y + Height;

    public double MidY => Y + (Height / 2);

    /// <summary>The smallest rectangle that contains both.</summary>
    public NormalisedRect Union(NormalisedRect other)
    {
        var minX = Math.Min(MinX, other.MinX);
        var minY = Math.Min(MinY, other.MinY);
        var maxX = Math.Max(MaxX, other.MaxX);
        var maxY = Math.Max(MaxY, other.MaxY);
        return new NormalisedRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// The smallest rectangle containing every one given, or <see cref="Zero"/>
    /// when there are none. A recogniser that reports words rather than lines
    /// (Windows.Media.Ocr) gives each line the box enclosing its words.
    /// </summary>
    public static NormalisedRect Enclosing(IEnumerable<NormalisedRect> rectangles)
    {
        using var enumerator = rectangles.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return Zero;
        }
        var result = enumerator.Current;
        while (enumerator.MoveNext())
        {
            result = result.Union(enumerator.Current);
        }
        return result;
    }

    /// <summary>
    /// A pixel rectangle with a top-left origin, as Windows.Media.Ocr reports
    /// one, in the same pixel space as <paramref name="imageWidth"/> and
    /// <paramref name="imageHeight"/> — the size of the bitmap that was
    /// recognised, after any scaling down.
    /// </summary>
    public static NormalisedRect FromPixels(
        double x,
        double y,
        double width,
        double height,
        double imageWidth,
        double imageHeight)
    {
        if (!(imageWidth > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), imageWidth, "The image must have a width.");
        }
        if (!(imageHeight > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(imageHeight), imageHeight, "The image must have a height.");
        }
        return new NormalisedRect(x / imageWidth, y / imageHeight, width / imageWidth, height / imageHeight);
    }

    /// <summary>
    /// A normalised box whose origin is at the bottom left, as Apple Vision
    /// reports one, flipped so its top edge is measured from the top.
    /// </summary>
    public static NormalisedRect FromBottomLeftOrigin(double x, double y, double width, double height)
    {
        var box = new NormalisedRect(x, y, width, height);
        return new NormalisedRect(box.MinX, 1 - box.MaxY, box.Width, box.Height);
    }
}
