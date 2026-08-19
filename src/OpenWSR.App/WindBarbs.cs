using System.Windows;
using System.Windows.Media;

namespace OpenWSR.App;

/// <summary>
/// Station-model wind barbs.
///
/// A barb is read, not measured: the staff points the way the wind comes *from*, and the
/// flags on it add up to the speed in knots — a pennant is fifty, a full barb ten, a half
/// barb five. It is worth drawing properly rather than printing a number, because a column
/// of barbs shows veering and shear at a glance in a way a column of figures never does.
/// </summary>
public static class WindBarbs
{
    private const double StaffLength = 26;
    private const double FlagLength = 9;
    private const double FlagSpacing = 4.5;

    /// <summary>Calm is a circle, by convention — no staff, because there is no direction.</summary>
    private const double CalmKnots = 2.5;

    /// <summary>
    /// Build a barb centred on the origin, ready to be positioned by the caller. Screen
    /// coordinates, so y increases downward and north is up.
    /// </summary>
    public static Geometry Build(double speedKnots, double fromDirectionDeg)
    {
        var group = new GeometryGroup();
        if (speedKnots < CalmKnots)
        {
            group.Children.Add(new EllipseGeometry(new Point(0, 0), 3.5, 3.5));
            return group;
        }

        // The staff runs from the plotting point toward where the wind is coming from.
        double radians = fromDirectionDeg * Math.PI / 180.0;
        var along = new Vector(Math.Sin(radians), -Math.Cos(radians));
        var across = new Vector(-along.Y, along.X);   // perpendicular, for the flags

        var tip = (Point)(along * StaffLength);
        group.Children.Add(new LineGeometry(new Point(0, 0), tip));

        // Round to the nearest five knots, which is the resolution a barb can express.
        int remaining = (int)(Math.Round(speedKnots / 5.0) * 5);
        double offset = 0;

        void Flag(double length, bool pennant)
        {
            var root = tip - along * offset;
            var end = root - along * FlagSpacing + across * length;
            if (pennant)
            {
                // A filled triangle back to the staff.
                var figure = new PathFigure { StartPoint = root, IsClosed = true, IsFilled = true };
                figure.Segments.Add(new LineSegment(end, true));
                figure.Segments.Add(new LineSegment(root - along * (FlagSpacing * 2), true));
                var path = new PathGeometry();
                path.Figures.Add(figure);
                group.Children.Add(path);
            }
            else
            {
                group.Children.Add(new LineGeometry(root, end));
            }
        }

        while (remaining >= 50)
        {
            Flag(FlagLength, pennant: true);
            offset += FlagSpacing * 2.6;
            remaining -= 50;
        }
        while (remaining >= 10)
        {
            Flag(FlagLength, pennant: false);
            offset += FlagSpacing;
            remaining -= 10;
        }
        if (remaining >= 5)
        {
            // A half barb never sits at the very tip, or it reads as a full one.
            if (offset == 0) offset = FlagSpacing;
            Flag(FlagLength / 2, pennant: false);
        }

        return group;
    }
}
