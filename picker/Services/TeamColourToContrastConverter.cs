using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Converts a MultiViewer team-colour hex string (no leading '#', e.g. "E80020")
/// into a contrasting Black or White brush, matching MV Live Timing's driver block:
/// light team colours (e.g. Mercedes teal) get a black companion, dark/saturated
/// colours (e.g. Ferrari red, Red Bull blue) get a white companion.
///
/// Used in two places on the same chip, both driven by the team colour so they stay
/// in lock-step: the position-number text (sits on the team colour) and the TLA inset
/// background (the TLA text itself is painted in the team colour on top of it).
/// </summary>
internal sealed class TeamColourToContrastConverter : IValueConverter
{
    private static readonly SolidColorBrush Black = new(Color.FromRgb(0x0A, 0x0A, 0x0E));
    private static readonly SolidColorBrush White = new(Color.FromRgb(0xFF, 0xFF, 0xFF));

    static TeamColourToContrastConverter()
    {
        Black.Freeze();
        White.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string hex = (value as string ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 ||
            !byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return White;

        // Rec. 601 luma. Threshold 140 puts Mercedes teal (~146) on the light side
        // (black companion) while Red Bull blue (~121) and Ferrari red (~86) stay dark
        // (white companion), matching MV Live Timing exactly.
        double luma = 0.299 * r + 0.587 * g + 0.114 * b;
        return luma >= 140 ? Black : White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
