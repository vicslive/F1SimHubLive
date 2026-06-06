using System;
using System.Globalization;
using System.Windows.Data;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Converts an integer position to its display string ("—" for 0/unknown).
/// </summary>
internal sealed class PositionToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int pos && pos > 0) return pos.ToString(CultureInfo.InvariantCulture);
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
