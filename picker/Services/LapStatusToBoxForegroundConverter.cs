using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using F1SimHubLive.Picker.Models;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Foreground brush used on top of <see cref="LapStatusToBoxBackgroundConverter"/>.
/// Contrasts cleanly against the chosen pill background:
///   None         = light gray (#E8E8EE) — no pill, default lap-time look
///   PersonalBest = BLACK on green pill
///   SessionBest  = WHITE on purple pill
/// </summary>
internal sealed class LapStatusToBoxForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Default = new(Color.FromRgb(0xE8, 0xE8, 0xEE));
    private static readonly SolidColorBrush Black = new(Color.FromRgb(0x0A, 0x0A, 0x0E));
    private static readonly SolidColorBrush White = new(Color.FromRgb(0xFF, 0xFF, 0xFF));

    static LapStatusToBoxForegroundConverter()
    {
        Default.Freeze(); Black.Freeze(); White.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is LapStatus s)
        {
            return s switch
            {
                LapStatus.SessionBest => White,
                LapStatus.PersonalBest => Black,
                LapStatus.InPit => White,
                _ => Default,
            };
        }
        return Default;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
