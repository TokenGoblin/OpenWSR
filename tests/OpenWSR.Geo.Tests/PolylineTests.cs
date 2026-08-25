using OpenWSR.Geo;

namespace OpenWSR.Geo.Tests;

/// <summary>
/// No reference implementation to check against, so these assert the properties that make
/// simplification safe to draw with: the ends never move, nothing kept strays further than
/// the tolerance, and the shape survives.
/// </summary>
public sealed class PolylineTests
{
    [Fact]
    public void AStraightRunCollapsesToItsEnds()
    {
        var line = Enumerable.Range(0, 100).Select(i => ((double)i, 0.0)).ToList();
        var simplified = Polyline.Simplify(line, 0.5);

        Assert.Equal(2, simplified.Count);
        Assert.Equal((0.0, 0.0), simplified[0]);
        Assert.Equal((99.0, 0.0), simplified[^1]);
    }

    [Fact]
    public void ACornerSurvives()
    {
        // A right angle is exactly what must not be smoothed away: it is the corner of a
        // county, and rounding it moves the border.
        List<(double, double)> line =
        [
            (0, 0), (1, 0), (2, 0), (3, 0),
            (3, 1), (3, 2), (3, 3),
        ];
        var simplified = Polyline.Simplify(line, 0.1);

        Assert.Equal(3, simplified.Count);
        Assert.Equal((3.0, 0.0), simplified[1]);
    }

    [Fact]
    public void TheEndsAreNeverMoved()
    {
        var random = new Random(1234);
        var line = Enumerable.Range(0, 500)
            .Select(_ => (random.NextDouble() * 100, random.NextDouble() * 100))
            .ToList();

        var simplified = Polyline.Simplify(line, 5);

        Assert.Equal(line[0], simplified[0]);
        Assert.Equal(line[^1], simplified[^1]);
    }

    [Fact]
    public void NothingDroppedStraysFurtherThanTheTolerance()
    {
        // The guarantee that makes a tolerance meaningful: every original point is still
        // within it of the simplified line, so the drawn shape is wrong by at most that.
        var random = new Random(99);
        double x = 0, y = 0;
        var line = new List<(double X, double Y)>();
        for (int i = 0; i < 800; i++)
        {
            x += random.NextDouble() * 2 - 1;
            y += random.NextDouble() * 2 - 1;
            line.Add((x, y));
        }

        const double tolerance = 1.5;
        var simplified = Polyline.Simplify(line, tolerance);
        Assert.True(simplified.Count < line.Count);

        foreach (var point in line)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < simplified.Count; i++)
                best = Math.Min(best, DistanceToSegment(point, simplified[i], simplified[i + 1]));
            Assert.True(best <= tolerance + 1e-9, $"a dropped point sat {best:F4} away");
        }
    }

    [Fact]
    public void ALongShallowCurveIsNotFlattenedToItsChord()
    {
        // The arc's ends are level, so its chord is the flat line between them. Measuring
        // point-to-point instead of point-to-chord would see 400 points each 0.1 from the
        // last and call the whole thing straight; it bends 20 units and has to survive.
        var arc = Enumerable.Range(0, 400)
            .Select(i => (i * 0.1, 20 * Math.Sin(Math.PI * i / 399.0)))
            .ToList();

        var simplified = Polyline.Simplify(arc, 0.5);

        // Roughly sqrt(amplitude / tolerance) points describe a smooth arc to a given
        // tolerance, so about 6 here — the claim is that it is not 2.
        Assert.True(simplified.Count > 4, $"the arc collapsed to {simplified.Count} points");
        Assert.True(simplified.Max(p => p.Y) > 19, "the top of the arc was flattened");
    }

    [Fact]
    public void AClosedRingKeepsItsShape()
    {
        // The first pass on a ring gets a zero-length chord, its ends being the same point.
        var ring = new List<(double, double)>();
        for (int i = 0; i <= 64; i++)
        {
            double a = 2 * Math.PI * i / 64;
            ring.Add((100 * Math.Cos(a), 100 * Math.Sin(a)));
        }

        var simplified = Polyline.Simplify(ring, 1.0);

        Assert.True(simplified.Count is > 4 and < 64, $"got {simplified.Count} points");
        Assert.Equal(ring[0], simplified[0]);
        Assert.Equal(ring[^1], simplified[^1]);
        Assert.All(simplified, p =>
            Assert.InRange(Math.Sqrt(p.Item1 * p.Item1 + p.Item2 * p.Item2), 99, 101));
    }

    [Fact]
    public void AZeroToleranceKeepsEverything()
    {
        var line = Enumerable.Range(0, 50).Select(i => ((double)i, i % 3.0)).ToList();
        Assert.Equal(50, Polyline.Simplify(line, 0).Count);
    }

    [Fact]
    public void ShortRunsAreReturnedIntact()
    {
        List<(double, double)> two = [(0, 0), (5, 5)];
        Assert.Equal(2, Polyline.Simplify(two, 100).Count);
        Assert.Empty(Polyline.Simplify([], 1));
    }

    [Fact]
    public void BoundsCoverEveryPoint()
    {
        List<(double, double)> points = [(3, -1), (-4, 7), (10, 2)];
        var (minX, minY, maxX, maxY) = Polyline.Bounds(points);

        Assert.Equal(-4, minX);
        Assert.Equal(-1, minY);
        Assert.Equal(10, maxX);
        Assert.Equal(7, maxY);
    }

    private static double DistanceToSegment(
        (double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-18) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double cx = p.X - (a.X + t * dx), cy = p.Y - (a.Y + t * dy);
        return Math.Sqrt(cx * cx + cy * cy);
    }
}
