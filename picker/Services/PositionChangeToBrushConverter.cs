using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Colour for the position-change indicator, matching F1 Live Timing:
///   &gt; 0  ->  green  (gained)
///   &lt; 0  ->  red    (lost)
///   = 0  ->  grey   (no change — the muted "−0")
/// Pairs with <see cref="PositionChangeToTextConverter"/>.
/// </summary>
internal sealed class PositionChangeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x3F, 0xD0, 0x6A));
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xE5, 0x3A, 0x3A));
    private static readonly SolidColorBrush Grey = new(Color.FromRgb(0x6F, 0x6F, 0x7A));

    static PositionChangeToBrushConverter()
    {
        Green.Freeze(); Red.Freeze(); Grey.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int n)
        {
            if (n > 0) return Green;
            if (n < 0) return Red;
        }
        return Grey;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
