using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using F1SimHubLive.Picker.Models;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Converts a <see cref="LapStatus"/> to the brush used for sector-time text.
/// Unlike <see cref="LapStatusToBrushConverter"/> (which defaults to white for
/// lap-time displays), this one defaults to MultiViewer's mustard-yellow —
/// the convention F1's official timing uses for "a completed sector time".
///   None         = yellow  (sector time as set; not improving, not session best)
///   PersonalBest = green   (driver just improved their PB for this sector)
///   SessionBest  = purple  (overall fastest sector time of the session)
/// Keeping a dedicated converter (instead of changing the LapStatus → brush
/// mapping in <c>LapStatusToBrushConverter</c>) preserves the white default
/// for the LAST/BEST lap-time columns, which should NOT be yellow.
/// </summary>
internal sealed class SectorStatusToBrushConverter : IValueConverter
{
    // MultiViewer-matched palette (mined from app.asar 2.7.3 — Material UI):
    //   notImproved    = yellow[600]  #FDD835
    //   personalFastest = green[500]  #4CAF50
    //   overallFastest  = purple[500] #9C27B0
    private static readonly SolidColorBrush Yellow = new(Color.FromRgb(0xFD, 0xD8, 0x35));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush Purple = new(Color.FromRgb(0x9C, 0x27, 0xB0));

    static SectorStatusToBrushConverter()
    {
        Yellow.Freeze(); Green.Freeze(); Purple.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LapStatus s)
        {
            return s switch
            {
                LapStatus.SessionBest => Purple,
                LapStatus.PersonalBest => Green,
                _ => Yellow,
            };
        }
        return Yellow;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
