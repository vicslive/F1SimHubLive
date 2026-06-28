using System;
using System.Globalization;
using System.Windows.Data;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Turns a net position change (GridPos - current Position) into the
/// arrow+number string F1 Live Timing shows next to each driver:
///   &gt; 0  ->  "▲N"  (positions gained)
///   &lt; 0  ->  "▼N"  (positions lost, N is the absolute value)
///   = 0  ->  "−0"  (no change)
/// Pair with <see cref="PositionChangeToBrushConverter"/> for the colour
/// and gate visibility on <c>DriverTimingRow.HasGridPos</c>.
/// </summary>
internal sealed class PositionChangeToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int n)
        {
            if (n > 0) return "\u25B2" + n;          // ▲N
            if (n < 0) return "\u25BC" + (-n);        // ▼N
        }
        return "\u22120";                              // −0
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
