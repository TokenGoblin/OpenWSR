using OpenWSR.Geo;

namespace OpenWSR.Geo.Tests;

/// <summary>
/// Regression cover for the proximity-alert distance bug: distance to a warning polygon
/// was measured to its <em>vertices</em>, so a storm passing alongside a long edge was
/// missed entirely. NWS warning polygons carry four to eight vertices, so long edges are
/// the normal case.
/// </summary>
public class RingDistanceTests
{
    /// <summary>A tall, thin box: 1° of latitude high, 0.02° of longitude wide, at the equator.</summary>
    private static readonly (double LatDeg, double LonDeg)[] TallBox =
    [
        (0.00, 0.00),
        (1.00, 0.00),
        (1.00, 0.02),
        (0.00, 0.02),
    ];

    [Fact]
    public void PointBesideALongEdgeIsNear_NotVertexDistant()
    {
        // Halfway up the box's western edge and 0.01° west of it. The nearest edge is
        // ~1.1 km away; the nearest vertex is ~55 km away. Measuring to vertices reported
        // the latter and suppressed the alert.
        double edgeM = GeoMath.DistanceToRingM(0.5, -0.01, TallBox);

        double nearestVertexM = TallBox
            .Select(v => GeoMath.DistanceM(0.5, -0.01, v.LatDeg, v.LonDeg))
            .Min();

        Assert.InRange(edgeM, 1000, 1200);
        Assert.True(nearestVertexM > 50_000,
            $"expected the nearest vertex to be far away, was {nearestVertexM:F0} m");
    }

    [Fact]
    public void PointInsideTheRingIsZero()
    {
        Assert.Equal(0, GeoMath.DistanceToRingM(0.5, 0.01, TallBox));
    }

    [Fact]
    public void PointBeyondAnEndcapMeasuresToTheNearestCorner()
    {
        // Directly below the box's south-west corner: the closest point on the ring is
        // that corner, so segment clamping must land on the endpoint.
        double d = GeoMath.DistanceToRingM(-0.02, -0.02, TallBox);
        double corner = GeoMath.DistanceM(-0.02, -0.02, 0.0, 0.0);
        Assert.Equal(corner, d, 1);
    }

    [Fact]
    public void SegmentDistanceClampsToBothEndpoints()
    {
        (double LatDeg, double LonDeg) a = (0, 0);
        (double LatDeg, double LonDeg) b = (0, 1);

        // Past the far end: must equal the distance to b, not a projection beyond it.
        double past = GeoMath.DistanceToSegmentM(0, 2, a, b);
        Assert.Equal(GeoMath.DistanceM(0, 2, 0, 1), past, 1);

        // Before the near end: must equal the distance to a.
        double before = GeoMath.DistanceToSegmentM(0, -1, a, b);
        Assert.Equal(GeoMath.DistanceM(0, -1, 0, 0), before, 1);
    }

    [Fact]
    public void PerpendicularDistanceMatchesTheGreatCircleDrop()
    {
        // A point due north of the middle of an east-west segment: the perpendicular
        // distance is the latitude difference, which at 0.25° is ~27.8 km.
        double d = GeoMath.DistanceToSegmentM(0.25, 0.5, (0, 0), (0, 1));
        Assert.Equal(GeoMath.DistanceM(0.25, 0.5, 0, 0.5), d, 1);
        Assert.InRange(d, 27_000, 28_500);
    }

    [Fact]
    public void DegenerateRingsDoNotThrow()
    {
        Assert.Equal(double.MaxValue, GeoMath.DistanceToRingM(0, 0, []));

        (double LatDeg, double LonDeg)[] single = [(1, 1)];
        Assert.Equal(GeoMath.DistanceM(0, 0, 1, 1), GeoMath.DistanceToRingM(0, 0, single), 1);
    }
}
