using System;
using System.Globalization;
using System.Windows.Data;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Dims an entire timing-tower row when the driver has retired, mirroring
/// F1 official Live Timing — out-of-race cars fade back so the eye skips
/// them. Bound to <c>DriverTimingRow.Retired</c> on the row's root Button
/// <c>Opacity</c>.
///   Retired = true  -> 0.42 (faded)
///   otherwise        -> 1.0  (full)
/// </summary>
internal sealed class RetiredToOpacityConverter : IValueConverter
{
    private const double Dimmed = 0.42;
    private const double Full = 1.0;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? Dimmed : Full;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
