using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using F1SimHubLive.Picker.Models;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Converts a <see cref="LapStatus"/> to the brush used for lap or sector
/// time text — white for plain, yellow for personal best, purple for
/// session best. Mirrors the color language of MultiViewer's own timing
/// screen so Vic doesn't have to retrain his eye.
/// </summary>
internal sealed class LapStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Plain = new(Color.FromRgb(0xE8, 0xE8, 0xEE));
    private static readonly SolidColorBrush Pb = new(Color.FromRgb(0x3F, 0xD0, 0x6A));   // green = personal best
    private static readonly SolidColorBrush Sb = new(Color.FromRgb(0xA0, 0x50, 0xE0));   // purple = session best

    static LapStatusToBrushConverter()
    {
        Plain.Freeze(); Pb.Freeze(); Sb.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LapStatus s)
        {
            return s switch
            {
                LapStatus.SessionBest => Sb,
                LapStatus.PersonalBest => Pb,
                _ => Plain,
            };
        }
        return Plain;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
