using OpenWSR.Geo;

namespace OpenWSR.Geo.Tests;

public class BeamPathTests
{
    [Fact]
    public void HalfDegreeAt100KmSlant()
    {
        // Analytic value of the 4/3-earth formula; published beam-height tables
        // show ~1.5 km at this range.
        var (ground, height) = GeoMath.BeamPath(100_000, 0.5 * Math.PI / 180.0);
        Assert.InRange(height, 1459, 1463);
        Assert.InRange(ground, 99_950, 100_000);
        Assert.True(ground < 100_000); // arc range is always below slant range
    }

    [Fact]
    public void HalfDegreeAt200KmSlant()
    {
        var (_, height) = GeoMath.BeamPath(200_000, 0.5 * Math.PI / 180.0);
        Assert.InRange(height, 4093, 4104); // analytic 4098.7; published tables: ~4.1 km
    }

    [Fact]
    public void ZeroElevationStillClimbsFromEarthCurvature()
    {
        var (_, height) = GeoMath.BeamPath(100_000, 0);
        // h ≈ r²/(2·ke·a) = 1e10 / 1.699e7 ≈ 588 m
        Assert.InRange(height, 583, 594);
    }

    [Fact]
    public void ZeroRangeIsZero()
    {
        var (ground, height) = GeoMath.BeamPath(0, 0.3);
        Assert.Equal(0, ground, 9);
        Assert.Equal(0, height, 9);
    }

    [Fact]
    public void HighElevationApproachesSlantTrig()
    {
        // At 45 deg and short range, height exceeds flat-earth trig by exactly the
        // curvature term (r·cosθ)²/(2·ke·a) ≈ 2.94 m; ground range shrinks slightly.
        var (ground, height) = GeoMath.BeamPath(10_000, Math.PI / 4);
        double flat = 10_000 * Math.Sin(Math.PI / 4);
        double curvature = flat * flat / (2 * GeoMath.EffectiveEarthRadiusM);
        Assert.Equal(flat + curvature, height, 0.01);
        // Ground range shrinks by ~flat·h/(ke·a) ≈ 5.9 m relative to flat-earth.
        double flatGround = 10_000 * Math.Cos(Math.PI / 4);
        Assert.Equal(flatGround - flatGround * height / GeoMath.EffectiveEarthRadiusM, ground, 0.01);
    }
}

public class OffsetTests
{
    [Fact]
    public void DueNorthOneDegreeOfArc()
    {
        double oneDegreeM = GeoMath.EarthRadiusM * Math.PI / 180.0;
        var (lat, lon) = GeoMath.Offset(35.0, -97.0, 0.0, oneDegreeM);
        Assert.Equal(36.0, lat, 9);
        Assert.Equal(-97.0, lon, 9);
    }

    [Fact]
    public void DueEastAtEquator()
    {
        double oneDegreeM = GeoMath.EarthRadiusM * Math.PI / 180.0;
        var (lat, lon) = GeoMath.Offset(0.0, 10.0, Math.PI / 2, oneDegreeM);
        Assert.Equal(0.0, lat, 9);
        Assert.Equal(11.0, lon, 9);
    }

    [Fact]
    public void DueEastAtHighLatitudeIsNotFlatEarth()
    {
        // Great-circle heading east from 60N: longitude advances ~2x the arc, and the
        // path bends equatorward. A flat-earth offset would keep lat exactly 60.
        double arc = 100_000;
        var (lat, lon) = GeoMath.Offset(60.0, 0.0, Math.PI / 2, arc);
        Assert.True(lat < 60.0);
        Assert.True(lon > 1.7 && lon < 1.81);
    }

    [Fact]
    public void RoundTripThroughDistanceAndBearing()
    {
        var (lat, lon) = GeoMath.Offset(35.333, -97.278, 1.234, 150_000);
        Assert.Equal(150_000, GeoMath.DistanceM(35.333, -97.278, lat, lon), 0.001);
        Assert.Equal(1.234, GeoMath.BearingRad(35.333, -97.278, lat, lon), 9);
    }

    [Fact]
    public void CrossesAntimeridianCleanly()
    {
        var (_, lon) = GeoMath.Offset(0.0, 179.5, Math.PI / 2, 200_000);
        Assert.InRange(lon, -180.0, -178.0);
    }
}

public class PointInRingTests
{
    private static readonly (double, double)[] Quad =
    [
        (35.0, -98.0), (36.0, -98.0), (36.0, -97.0), (35.0, -97.0),
    ];

    [Theory]
    [InlineData(35.5, -97.5, true)]   // center
    [InlineData(35.99, -97.99, true)] // near corner, inside
    [InlineData(36.5, -97.5, false)]  // north of it
    [InlineData(35.5, -96.5, false)]  // east of it
    public void QuadContainment(double lat, double lon, bool expected) =>
        Assert.Equal(expected, GeoMath.PointInRing(lat, lon, Quad));

    [Fact]
    public void ConcavePolygonNotch()
    {
        // C-shape opening east: the notch is outside even though it is inside the bbox.
        (double, double)[] cShape =
        [
            (35.0, -98.0), (37.0, -98.0), (37.0, -96.0), (36.5, -96.0),
            (36.5, -97.5), (35.5, -97.5), (35.5, -96.0), (35.0, -96.0),
        ];
        Assert.False(GeoMath.PointInRing(36.0, -96.5, cShape)); // in the notch
        Assert.True(GeoMath.PointInRing(36.0, -97.75, cShape)); // in the spine
        Assert.True(GeoMath.PointInRing(36.75, -96.5, cShape)); // in the upper arm
    }
}

public class MercatorTests
{
    [Fact]
    public void OriginMapsToOrigin()
    {
        var (x, y) = GeoMath.ToMercator(0, 0);
        Assert.Equal(0, x, 6); // meters; sub-micron float noise from tan(pi/4) is fine
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void WebMercatorSquareExtent()
    {
        // The canonical EPSG:3857 bound: lat 85.05112878 maps to y == x(180 deg).
        var (x, _) = GeoMath.ToMercator(0, 180);
        var (_, y) = GeoMath.ToMercator(85.05112878, 0);
        Assert.Equal(20037508.342789244, x, 6);
        Assert.Equal(x, y, 3);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(35.333, -97.278)]
    [InlineData(64.85, -147.87)]   // PAPD, far north
    [InlineData(-45.0, 170.5)]
    [InlineData(84.9, 179.999)]
    public void RoundTripBelowNanodegree(double lat, double lon)
    {
        var (x, y) = GeoMath.ToMercator(lat, lon);
        var (lat2, lon2) = GeoMath.FromMercator(x, y);
        Assert.True(Math.Abs(lat - lat2) < 1e-9, $"lat error {Math.Abs(lat - lat2)}");
        Assert.True(Math.Abs(lon - lon2) < 1e-9, $"lon error {Math.Abs(lon - lon2)}");
    }
}
