using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CanvasCountdown.App.Platform;

/// <summary>
/// Draws a countdown tile — tray icon, taskbar badge or Settings preview —
/// exactly as <see cref="DockTileRenderPlan"/> lays it out.
/// </summary>
/// <remarks>
/// The plan decides everything: which text, how big, where, in which colours.
/// This only executes it, with text metrics measured in the same face it then
/// draws with (<see cref="GdiTileTextMetrics"/>), so what the plan measured is
/// what appears.
/// </remarks>
internal static class TileRenderer
{
    public static Bitmap Render(DockAppearance appearance, int? daysRemaining, string? label, int pixelSize)
    {
        var plan = DockTileRenderPlan.Make(appearance, daysRemaining, label, pixelSize, scale: 1, GdiTileTextMetrics.Shared);
        var bitmap = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = pixelSize <= 32 ? TextRenderingHint.AntiAliasGridFit : TextRenderingHint.AntiAlias;

        var tile = plan.TileRect;
        using (var path = RoundedRectangle(tile, plan.CornerRadius))
        using (var brush = new LinearGradientBrush(
            new PointF((float)tile.X, (float)tile.Y),
            new PointF((float)tile.X, (float)(tile.Y + tile.Height)),
            ToColor(plan.GradientTopColor),
            ToColor(plan.GradientBottomColor)))
        {
            graphics.FillPath(brush, path);
        }

        if (plan.Header is { } header)
        {
            DrawText(graphics, header, header: true, shadow: null);
        }
        DrawText(graphics, plan.Countdown, header: false, shadow: plan.CountdownShadow);
        return bitmap;
    }

    /// <summary>An icon handle for the tray or the taskbar. The caller owns it.</summary>
    public static IntPtr RenderIcon(DockAppearance appearance, int? daysRemaining, string? label, int pixelSize)
    {
        using var bitmap = Render(appearance, daysRemaining, label, pixelSize);
        return bitmap.GetHicon();
    }

    /// <summary>PNG bytes, for the Settings preview.</summary>
    public static byte[] RenderPng(DockAppearance appearance, int? daysRemaining, string? label, int pixelSize)
    {
        using var bitmap = Render(appearance, daysRemaining, label, pixelSize);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static void DrawText(Graphics graphics, DockTileText text, bool header, DockTileShadow? shadow)
    {
        using var font = GdiTileTextMetrics.Font(text.FontSize, text.FontWeight, header);
        var origin = new PointF((float)text.Origin.X, (float)text.Origin.Y);
        if (shadow is { Opacity: > 0 } && text.FontSize >= 24)
        {
            using var shadowBrush = new SolidBrush(ToColor(shadow.Color, shadow.Opacity));
            DrawRun(graphics, text, font, shadowBrush, new PointF(origin.X + (float)shadow.OffsetX, origin.Y + (float)shadow.OffsetY));
        }
        using var brush = new SolidBrush(ToColor(text.Color));
        DrawRun(graphics, text, font, brush, origin);
    }

    /// <summary>Character by character when the plan asks for tracking, which GDI+ cannot add by itself.</summary>
    private static void DrawRun(Graphics graphics, DockTileText text, Font font, Brush brush, PointF origin)
    {
        var format = StringFormat.GenericTypographic;
        if (Math.Abs(text.Tracking) < 0.01)
        {
            graphics.DrawString(text.Text, font, brush, origin, format);
            return;
        }
        var x = origin.X;
        foreach (var character in text.Text)
        {
            var glyph = character.ToString();
            graphics.DrawString(glyph, font, brush, new PointF(x, origin.Y), format);
            x += graphics.MeasureString(glyph, font, PointF.Empty, format).Width + (float)text.Tracking;
        }
    }

    private static GraphicsPath RoundedRectangle(TileRect rect, double radius)
    {
        var path = new GraphicsPath();
        var r = (float)Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var x = (float)rect.X;
        var y = (float)rect.Y;
        var width = (float)rect.Width;
        var height = (float)rect.Height;
        if (r <= 0.01f)
        {
            path.AddRectangle(new RectangleF(x, y, width, height));
            return path;
        }
        var diameter = r * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color ToColor(SrgbColor color, double opacity = 1)
    {
        var (red, green, blue) = color.ToBytes();
        return Color.FromArgb((int)Math.Round(Math.Clamp(opacity, 0, 1) * 255), red, green, blue);
    }
}

/// <summary>Text measured in the faces the tile is drawn with.</summary>
internal sealed class GdiTileTextMetrics : IDockTileTextMetrics
{
    public static GdiTileTextMetrics Shared { get; } = new();

    private static readonly Lazy<Graphics> Measurer = new(() => Graphics.FromImage(new Bitmap(1, 1)));

    /// <summary>
    /// Segoe UI is on every Windows 10 and 11 PC. 800-weight numbers use its
    /// Black cut, the nearest GDI+ can draw; Windows has no rounded system face,
    /// so the plan's preference for one is not met here.
    /// </summary>
    public static Font Font(double size, int weight, bool header)
    {
        var pixels = (float)Math.Max(1, size);
        if (!header && weight >= 800 && IsInstalled("Segoe UI Black"))
        {
            return new Font("Segoe UI Black", pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        return new Font("Segoe UI", pixels, weight >= 600 ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public double CountdownWidth(string text, double fontSize, DockNumberWeight weight)
    {
        using var font = Font(fontSize, weight.FontWeight(), header: false);
        lock (Measurer)
        {
            return Measurer.Value.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        }
    }

    public double CountdownCapHeight(double fontSize, DockNumberWeight weight) => fontSize * 0.70;

    public double CountdownAscender(double fontSize, DockNumberWeight weight)
    {
        using var font = Font(fontSize, weight.FontWeight(), header: false);
        var family = font.FontFamily;
        return fontSize * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style);
    }

    public TileSize LabelSize(string text, double fontSize, double tracking)
    {
        using var font = Font(fontSize, 700, header: true);
        lock (Measurer)
        {
            var size = Measurer.Value.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            return new TileSize(size.Width + (tracking * text.Length), font.GetHeight(Measurer.Value));
        }
    }

    private static bool IsInstalled(string family)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(installed => string.Equals(installed.Name, family, StringComparison.OrdinalIgnoreCase));
    }
}
