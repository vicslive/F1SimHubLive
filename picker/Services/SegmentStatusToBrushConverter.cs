using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Converts a MultiViewer segment-status integer to the brush used for one
/// mini-sector tile. Standard F1 SignalR segment-status codes:
/// 0 = no data, 2048 = yellow (set, not improved), 2049 = green (personal
/// best in segment), 2051 = purple (overall best in segment),
/// 2064 = blue (pit lane — in-lap / out-lap). Verified live against MV
/// (Austrian GP P2, 2026): in-pit cars report 2064 on their pit segments,
/// which live timing renders blue.
/// </summary>
internal sealed class SegmentStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Dark = new(Color.FromRgb(0x2A, 0x2A, 0x33));
    private static readonly SolidColorBrush Yellow = new(Color.FromRgb(0xF5, 0xC5, 0x18));
    private static readonly SolidColorBrush Purple = new(Color.FromRgb(0xA0, 0x50, 0xE0));
    private static readonly SolidColorBrush Blue = new(Color.FromRgb(0x3C, 0x9C, 0xF0));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x3F, 0xD0, 0x6A));

    static SegmentStatusToBrushConverter()
    {
        Dark.Freeze(); Yellow.Freeze(); Purple.Freeze(); Blue.Freeze(); Green.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int code)
        {
            return code switch
            {
                2048 => Yellow,
                2049 => Green,
                2051 => Purple,
                2064 => Blue,
                _ => Dark,
            };
        }
        return Dark;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
