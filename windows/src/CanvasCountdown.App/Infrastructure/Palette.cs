using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CanvasCountdown.App;

/// <summary>Colours the window draws with, from theme resources so dark mode follows Windows.</summary>
public static class Palette
{
    public static Brush ForTone(DayCountTone tone) => tone switch
    {
        DayCountTone.Late => Theme("SystemFillColorCriticalBrush"),
        DayCountTone.Today => Theme("SystemFillColorCautionBrush"),
        DayCountTone.Muted => Theme("TextFillColorSecondaryBrush"),
        _ => Theme("TextFillColorPrimaryBrush"),
    };

    public static Brush ForTitle(bool isIgnored) => Theme(isIgnored ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");

    public static Brush FromHex(string? hex) => new SolidColorBrush(ColorFromHex(hex));

    /// <summary>"#RRGGBB" or "#RRGGBBAA", as the labels and themes store them. Anything else is transparent.</summary>
    public static Color ColorFromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Colors.Transparent;
        }
        var value = hex.Trim().TrimStart('#');
        if (value.Length is not (6 or 8) || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return Colors.Transparent;
        }
        return value.Length == 6
            ? Color.FromArgb(0xFF, (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed)
            : Color.FromArgb((byte)packed, (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8));
    }

    public static string HexFromColor(Color color)
        => color.A == 0xFF
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

    private static Brush Theme(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Colors.Gray);
}
