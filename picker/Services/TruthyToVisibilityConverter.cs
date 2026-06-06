using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// True/non-empty -> Visible, otherwise Collapsed. Used to hide the
/// interval column for the leader and the gap column when no leader is
/// established yet.
/// </summary>
internal sealed class TruthyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int n => n != 0,
            _ => true,
        };
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
