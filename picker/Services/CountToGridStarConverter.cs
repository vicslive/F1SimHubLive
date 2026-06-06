using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Converts an integer count to a star-sized <see cref="GridLength"/>,
/// e.g. <c>3</c> → <c>3*</c>. Used by the per-row sector strip so the
/// three sector sub-columns (S1, S2, S3) split the available width
/// proportionally to their actual mini-sector counts.
///
/// <para>Why: F1 tracks have non-uniform sector segment counts (Imola
/// has roughly 3 mini-sectors in S1, 7 in S2, 3 in S3). With a fixed
/// <c>*,*,*</c> split each sector gets the same column width, so S2's
/// 7 segments end up visually skinnier than S1's 3 — the per-segment
/// bar width drifts across sectors. Weighting each column by segment
/// count keeps the per-segment pixel width identical across all three
/// sectors, which is what Vic noticed comparing S1 (looked right) to S2
/// (looked squeezed).</para>
///
/// <para>A minimum weight of 1 is enforced so that pre-race sectors
/// (Segments collection empty until the driver has run at least one lap)
/// still claim some non-zero column width rather than collapsing the
/// whole strip. Once telemetry starts arriving the binding refreshes
/// via the <c>SectorView.SegmentCount</c> INPC notification.</para>
/// </summary>
internal sealed class CountToGridStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value switch
        {
            int n => n,
            null => 0,
            IConvertible c => c.ToInt32(culture),
            _ => 0,
        };
        // Guard against zero-width column when no segments yet (pre-race
        // or before the first lap completes). Star weight of 1 still
        // gives a visible placeholder column.
        if (count < 1) count = 1;
        return new GridLength(count, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
