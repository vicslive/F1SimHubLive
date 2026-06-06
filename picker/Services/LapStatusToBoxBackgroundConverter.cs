using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using F1SimHubLive.Picker.Models;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Background brush for the LAST/BEST lap-time "pill". Mirrors F1's
/// official-timing convention so a freshly-set PB or SB lap is immediately
/// recognisable at a glance, not just a subtle text-color shift.
///   None         = transparent (no pill, just default text color)
///   PersonalBest = green pill (driver just improved their PB)
///   SessionBest  = purple pill (overall fastest lap of the session)
/// Pair with <see cref="LapStatusToBoxForegroundConverter"/> for the text
/// color on top of the pill.
/// </summary>
internal sealed class LapStatusToBoxBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);
    // MultiViewer-matched palette (Material UI green[500] / purple[500])
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush Purple = new(Color.FromRgb(0x9C, 0x27, 0xB0));

    static LapStatusToBoxBackgroundConverter()
    {
        Transparent.Freeze(); Green.Freeze(); Purple.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LapStatus s)
        {
            return s switch
            {
                LapStatus.SessionBest => Purple,
                LapStatus.PersonalBest => Green,
                _ => Transparent,
            };
        }
        return Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
