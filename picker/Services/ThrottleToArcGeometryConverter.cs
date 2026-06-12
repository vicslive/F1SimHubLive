using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Converts a throttle percentage (0–100, double) into a <see cref="PathGeometry"/>
/// arc that sweeps clockwise around the per-driver gear-cluster ring.
///
/// <para>The arc starts at <c>StartAngleDegrees</c> (broadcast convention:
/// ~140°, i.e. roughly 8 o'clock in screen coords with Y pointing down),
/// sweeps clockwise by <c>throttle/100 * MaxSweepDegrees</c> (default 260°,
/// leaving a small gap at the bottom of the ring for visual balance), and
/// is rendered as a stroked arc only — no fill. The host <c>Path</c> sets
/// the stroke colour, thickness, and line caps.</para>
///
/// <para>Bound from <c>DriverTimingRow.Throttle</c> in <c>MainWindow.xaml</c>'s
/// per-row cluster (added in v1.7.5).</para>
///
/// <para>The geometry is frozen before return so it is safe to share across
/// rows and across threads.</para>
/// </summary>
public sealed class ThrottleToArcGeometryConverter : IValueConverter
{
    /// <summary>Center X of the host 34×34 cluster Grid.</summary>
    public double CenterX { get; set; } = 17;
    /// <summary>Center Y of the host 34×34 cluster Grid.</summary>
    public double CenterY { get; set; } = 17;
    /// <summary>
    /// Arc radius. Slightly inside the ellipse stroke so the arc visually
    /// sits inside the dark ring border instead of overlapping it.
    /// </summary>
    public double Radius { get; set; } = 14;
    /// <summary>
    /// Starting angle in degrees. Screen-coord convention (Y points down):
    /// 0° = +X (3 o'clock), 90° = +Y (6 o'clock), 180° = -X (9 o'clock),
    /// 270° = -Y (12 o'clock). 140° puts the start at ~8 o'clock.
    /// </summary>
    public double StartAngleDegrees { get; set; } = 140;
    /// <summary>
    /// Maximum sweep at 100 % throttle. 260° leaves an 80° gap at the bottom
    /// of the ring — matches the broadcast "near-full ring with bottom gap"
    /// pattern shown in F1 Live Timing.
    /// </summary>
    public double MaxSweepDegrees { get; set; } = 260;
    /// <summary>
    /// Below this throttle %, render an empty geometry (no stroke at all).
    /// Prevents a tiny "dot" artifact at 0 % from a near-zero arc.
    /// </summary>
    public double MinVisibleThrottle { get; set; } = 0.5;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return Geometry.Empty;
        double throttle = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0
        };
        if (throttle < MinVisibleThrottle) return Geometry.Empty;
        if (throttle > 100) throttle = 100;

        double sweepDeg = throttle / 100.0 * MaxSweepDegrees;
        double endAngleDeg = StartAngleDegrees + sweepDeg;

        double startRad = StartAngleDegrees * Math.PI / 180.0;
        double endRad = endAngleDeg * Math.PI / 180.0;

        var startPt = new Point(
            CenterX + Radius * Math.Cos(startRad),
            CenterY + Radius * Math.Sin(startRad));
        var endPt = new Point(
            CenterX + Radius * Math.Cos(endRad),
            CenterY + Radius * Math.Sin(endRad));

        bool isLarge = sweepDeg > 180;

        var fig = new PathFigure
        {
            StartPoint = startPt,
            IsClosed = false,
            IsFilled = false,
        };
        fig.Segments.Add(new ArcSegment(
            point: endPt,
            size: new Size(Radius, Radius),
            rotationAngle: 0,
            isLargeArc: isLarge,
            sweepDirection: SweepDirection.Clockwise,
            isStroked: true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("ThrottleToArcGeometryConverter is one-way.");
}
