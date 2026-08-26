using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.App;

/// <summary>
/// Draws the wind profile as a hodograph.
/// </summary>
/// <remarks>
/// The barb list beside this says what the wind is doing at each height. The hodograph says
/// what the <em>profile</em> is doing, which is a different question and the one that matters
/// for rotation: the length of the trace is shear and its curvature is the streamwise
/// vorticity a storm tilts into a mesocyclone. Neither is readable from a column of numbers.
///
/// <para>Drawn as WPF geometry rather than into the D3D scene because it lives in a docked
/// panel, not over the map — the airspace rule only bites for things that must appear above
/// the child HWND.</para>
/// </remarks>
public static class HodographPlot
{
    /// <summary>
    /// Height bands, coloured so the layer that matters most reads first.
    ///
    /// The lowest kilometre is where tornado-relevant helicity lives, so it is the brightest;
    /// everything above fades back. Bands are in metres above the profile's own base.
    /// </summary>
    private static readonly (double TopM, Color Colour, string Label)[] Bands =
    [
        (1000, Color.FromRgb(0xFF, 0x5A, 0x5A), "0–1 km"),
        (3000, Color.FromRgb(0xFF, 0xB0, 0x3A), "1–3 km"),
        (6000, Color.FromRgb(0x6A, 0xE0, 0x7A), "3–6 km"),
        (double.MaxValue, Color.FromRgb(0x6A, 0xC8, 0xF0), "6 km+"),
    ];

    private static readonly Color Grid = Color.FromRgb(0x3A, 0x40, 0x4C);
    private static readonly Color Axis = Color.FromRgb(0x55, 0x5D, 0x6B);
    private static readonly Color Text = Color.FromRgb(0x9A, 0xA2, 0xAE);
    private static readonly Color Storm = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>
    /// Render the plot, or null when there is nothing to draw.
    /// </summary>
    /// <param name="points">Wind components by height, lowest first.</param>
    /// <param name="stormMotion">
    /// Observed storm motion in m/s, when a cell is being tracked. Helicity is measured
    /// relative to it, so it is drawn: a reader who cannot see the point everything is
    /// measured from cannot check the number against the picture.
    /// </param>
    /// <param name="sizePx">Edge of the square plot.</param>
    /// <param name="knots">Label rings in knots rather than m/s.</param>
    public static DrawingImage? Build(
        IReadOnlyList<HodographPoint> points,
        (double UMs, double VMs)? stormMotion,
        double sizePx,
        bool knots)
    {
        if (points.Count < 2) return null;

        double toDisplay = knots ? 1.943844 : 1.0;
        double reach = points.Max(p => Math.Sqrt(p.UMs * p.UMs + p.VMs * p.VMs)) * toDisplay;
        if (stormMotion is { } motion)
            reach = Math.Max(reach,
                Math.Sqrt(motion.UMs * motion.UMs + motion.VMs * motion.VMs) * toDisplay);

        // Round the reach up to a whole ring so the outermost ring is never clipped, and
        // never let it collapse on a calm profile.
        double step = knots ? 10 : 5;
        int rings = Math.Max(2, (int)Math.Ceiling(reach / step));
        double full = rings * step;

        double half = sizePx / 2;
        double scale = (half - 10) / full;   // 10 px of margin for the outer ring's label

        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            // A transparent backdrop fixes the bounds; without it the DrawingImage sizes
            // itself to the ink and the plot jumps around as the profile changes.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, sizePx, sizePx));

            var gridPen = new Pen(new SolidColorBrush(Grid), 1) { DashStyle = DashStyles.Dot };
            gridPen.Freeze();
            for (int i = 1; i <= rings; i++)
            {
                double r = i * step * scale;
                dc.DrawEllipse(null, gridPen, new Point(half, half), r, r);
                Label(dc, $"{i * step:F0}", half + r - 1, half - 11, Text, 9, right: true);
            }

            var axisPen = new Pen(new SolidColorBrush(Axis), 1);
            axisPen.Freeze();
            dc.DrawLine(axisPen, new Point(half - full * scale, half), new Point(half + full * scale, half));
            dc.DrawLine(axisPen, new Point(half, half - full * scale), new Point(half, half + full * scale));

            // The trace, one polyline per band so the colour changes at the boundary rather
            // than at whichever level happens to sit nearest it.
            double baseM = points[0].AltitudeM;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                var pen = new Pen(new SolidColorBrush(BandColour(a.AltitudeM - baseM)), 2.4)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                pen.Freeze();
                dc.DrawLine(pen, Project(a, half, scale, toDisplay), Project(b, half, scale, toDisplay));
            }

            // Height ticks at each kilometre, so the curve can be read for height as well
            // as for shape.
            var tickBrush = new SolidColorBrush(Text);
            tickBrush.Freeze();
            for (double km = 1; km <= 12; km++)
            {
                var at = Sample(points, baseM + km * 1000);
                if (at is not { } p) break;
                var centre = Project(p, half, scale, toDisplay);
                dc.DrawEllipse(tickBrush, null, centre, 1.8, 1.8);
            }

            if (stormMotion is { } sm)
            {
                var centre = Project(
                    new HodographPoint(0, sm.UMs, sm.VMs), half, scale, toDisplay);
                var pen = new Pen(new SolidColorBrush(Storm), 1.4);
                pen.Freeze();
                dc.DrawEllipse(null, pen, centre, 4, 4);
                dc.DrawLine(pen, new Point(centre.X - 6, centre.Y), new Point(centre.X + 6, centre.Y));
                dc.DrawLine(pen, new Point(centre.X, centre.Y - 6), new Point(centre.X, centre.Y + 6));
            }
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Screen position of a wind vector.
    /// </summary>
    /// <remarks>
    /// East is right and north is <em>up</em>, so the vertical axis is negated: WPF's y grows
    /// downward and a hodograph drawn without that flip is mirrored, which turns a veering
    /// profile into a backing one and reverses the sign of everything it is read for.
    /// </remarks>
    private static Point Project(HodographPoint p, double half, double scale, double toDisplay) =>
        new(half + p.UMs * toDisplay * scale, half - p.VMs * toDisplay * scale);

    private static Color BandColour(double heightAboveBaseM)
    {
        foreach (var (top, colour, _) in Bands)
            if (heightAboveBaseM < top)
                return colour;
        return Bands[^1].Colour;
    }

    /// <summary>The wind at an exact height, interpolated, or null when it is off the top.</summary>
    private static HodographPoint? Sample(IReadOnlyList<HodographPoint> points, double altitudeM)
    {
        if (points.Count == 0 || altitudeM > points[^1].AltitudeM) return null;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            if (points[i + 1].AltitudeM < altitudeM) continue;
            double span = points[i + 1].AltitudeM - points[i].AltitudeM;
            double t = span <= 0 ? 0 : (altitudeM - points[i].AltitudeM) / span;
            return new HodographPoint(
                altitudeM,
                points[i].UMs + t * (points[i + 1].UMs - points[i].UMs),
                points[i].VMs + t * (points[i + 1].VMs - points[i].VMs));
        }
        return null;
    }

    private static void Label(
        DrawingContext dc, string text, double x, double y, Color colour, double size, bool right)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), size, brush, 1.0);
        dc.DrawText(formatted, new Point(right ? x - formatted.Width : x, y));
    }
}
