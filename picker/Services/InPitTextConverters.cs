using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Renders the "IN PIT" indicator as a vivid red pill, matching the
/// hard-to-miss treatment F1 official live-timing uses. Returns red when
/// the bound text is exactly "IN PIT" (the literal we inject for pit-bound
/// drivers in <c>LiveTimingClient.ApplyOrdered</c>), otherwise transparent
/// so the field looks normal.
///
/// Pairs with <see cref="InPitTextToForegroundConverter"/> for the
/// white text on the red box.
/// </summary>
internal sealed class InPitTextToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Transparent = new(Colors.Transparent);
    // MultiViewer-matched (Material UI red[500])
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xF4, 0x43, 0x36));

    static InPitTextToBackgroundConverter()
    {
        Transparent.Freeze(); Red.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) == "IN PIT" ? Red : Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Companion to <see cref="InPitTextToBackgroundConverter"/>. Forces white
/// text when the bound value is "IN PIT" so it reads on the red pill;
/// otherwise returns the default INT/LDR grey so non-pit gaps render
/// the same as before this change.
/// </summary>
internal sealed class InPitTextToForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Default = new(Color.FromRgb(0xC8, 0xC8, 0xD0));
    private static readonly SolidColorBrush White = new(Color.FromRgb(0xFF, 0xFF, 0xFF));

    static InPitTextToForegroundConverter()
    {
        Default.Freeze(); White.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) == "IN PIT" ? White : Default;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
