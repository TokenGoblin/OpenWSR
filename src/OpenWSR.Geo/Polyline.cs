namespace OpenWSR.Geo;

/// <summary>Operations on an open or closed run of planar points.</summary>
public static class Polyline
{
    /// <summary>
    /// Ramer-Douglas-Peucker: drop every point that sits within <paramref name="tolerance"/>
    /// of the line its neighbours already describe.
    /// </summary>
    /// <remarks>
    /// Boundary data is drawn at every zoom from a continent to a street, and it is stored at
    /// the resolution the finest of those needs — the Census county file is 1.03 million
    /// points, which at national zoom is roughly two hundred points per pixel. Feeding that
    /// to the overlay renderer costs six million vertices a frame to draw a line no thicker
    /// than it would be from a few thousand.
    ///
    /// <para>Simplification has to happen in the plane the drawing happens in, so callers
    /// project to Mercator first and pass a tolerance in Mercator metres. Doing it in degrees
    /// would thin the north of a shape harder than the south, because a degree of longitude
    /// is not a fixed distance.</para>
    ///
    /// <para>Distance is measured perpendicular to the chord rather than between consecutive
    /// points. Dropping points that are merely close together destroys a long shallow curve —
    /// a coastline — because none of its points is far from its neighbour even though the run
    /// of them bends a long way.</para>
    /// </remarks>
    public static List<(double X, double Y)> Simplify(
        IReadOnlyList<(double X, double Y)> points, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count <= 2 || tolerance <= 0) return [.. points];

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;

        // An explicit stack, not recursion: a pathological run degenerates to one point per
        // level, and boundary files are not obliged to be well behaved.
        var pending = new Stack<(int From, int To)>();
        pending.Push((0, points.Count - 1));
        double squared = tolerance * tolerance;

        while (pending.Count > 0)
        {
            var (from, to) = pending.Pop();
            if (to <= from + 1) continue;

            double worst = -1;
            int at = -1;
            for (int i = from + 1; i < to; i++)
            {
                double d = SquaredDistanceToSegment(points[i], points[from], points[to]);
                if (d <= worst) continue;
                worst = d;
                at = i;
            }

            if (worst <= squared || at < 0) continue;
            keep[at] = true;
            pending.Push((from, at));
            pending.Push((at, to));
        }

        var kept = new List<(double X, double Y)>();
        for (int i = 0; i < points.Count; i++)
            if (keep[i])
                kept.Add(points[i]);
        return kept;
    }

    /// <summary>The axis-aligned bounds of a run of points.</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) Bounds(
        IReadOnlyList<(double X, double Y)> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in points)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        return (minX, minY, maxX, maxY);
    }

    private static double SquaredDistanceToSegment(
        (double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;

        // A closed ring hands the first pass a zero-length chord, its two ends being the
        // same point. Falling back to the distance from that point keeps the split going
        // instead of keeping nothing.
        if (lengthSquared < 1e-18)
        {
            double ex = p.X - a.X, ey = p.Y - a.Y;
            return ex * ex + ey * ey;
        }

        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared;
        t = Math.Clamp(t, 0, 1);
        double cx = p.X - (a.X + t * dx), cy = p.Y - (a.Y + t * dy);
        return cx * cx + cy * cy;
    }
}
