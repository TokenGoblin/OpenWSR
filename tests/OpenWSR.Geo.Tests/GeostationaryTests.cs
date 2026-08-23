using OpenWSR.Geo;

namespace OpenWSR.Geo.Tests;

/// <summary>
/// The GOES-R ABI fixed grid, checked against <b>pyproj 3.7.2</b> driving PROJ's own
/// <c>geos</c> implementation — an independent implementation, which is the standard this
/// project holds projections to.
///
/// pyproj works in metres on the perspective plane where ABI files store radians; the two
/// differ by exactly the perspective point height, so every reference value below was
/// printed as <c>x / 35786023</c>. The CRS was built with <c>CRS.from_cf</c> from the same
/// attribute names and values the ABI file carries.
/// </summary>
public class GeostationaryTests
{
    /// <summary>GOES-East: the projection the committed ABI file describes.</summary>
    private static Geostationary GoesEast() => Geostationary.GoesR(-75.0);

    /// <summary>Scan angles are ~1e-1 radians, so this is about a metre on the ground.</summary>
    private const double Tolerance = 1e-9;

    [Theory]
    // lat, lon, then pyproj's x and y in radians.
    [InlineData(0.0, -75.0, +0.000000000, +0.000000000)]
    [InlineData(40.300, -111.900, -0.075846168, +0.106819067)]
    [InlineData(25.000, -80.000, -0.013810126, +0.073464141)]
    [InlineData(49.000, -97.000, -0.040690130, +0.124507621)]
    [InlineData(30.000, -90.000, -0.038692768, +0.085882892)]
    [InlineData(45.000, -123.000, -0.085008377, +0.114146492)]
    public void ForwardMatchesProj(double latDeg, double lonDeg, double x, double y)
    {
        var scan = GoesEast().Forward(latDeg, lonDeg);

        Assert.NotNull(scan);
        Assert.Equal(x, scan!.Value.X, Tolerance);
        Assert.Equal(y, scan.Value.Y, Tolerance);
    }

    [Theory]
    // x, y in radians, then pyproj's latitude and longitude.
    [InlineData(+0.000000, +0.000000, +0.000000000, -75.000000000)]
    [InlineData(-0.050000, +0.080000, +27.754421528, -94.105572931)]
    [InlineData(+0.030000, -0.020000, -6.511453082, -65.205005843)]
    public void InverseMatchesProj(double x, double y, double latDeg, double lonDeg)
    {
        var ground = GoesEast().Inverse(x, y);

        Assert.NotNull(ground);
        Assert.Equal(latDeg, ground!.Value.LatDeg, 7);
        Assert.Equal(lonDeg, ground.Value.LonDeg, 7);
    }

    /// <summary>
    /// The CONUS sector is a rectangle in scan angle, and a rectangle on a sphere seen edge-on
    /// has corners that are not on it. This exact pair is the file's own <c>x</c>/<c>y</c>
    /// add_offset — the top-left corner of the grid — and pyproj returns infinity for it.
    /// A resampler that trusts the four corners to be real places gets a garbage bounding box.
    /// </summary>
    [Fact]
    public void TheCornerOfTheConusGridIsNotOnTheEarth()
    {
        Assert.Null(GoesEast().Inverse(-0.101332, 0.128212));
    }

    /// <summary>
    /// Half of what the satellite can be pointed at is behind the planet. Those points still
    /// solve to plausible-looking scan angles if the visibility test is skipped, which paints
    /// Asia over North America rather than failing.
    /// </summary>
    [Theory]
    [InlineData(0.0, 105.0)]     // antipodal to the sub-satellite point
    [InlineData(0.0, 60.0)]      // just past the eastern limb
    [InlineData(89.0, -75.0)]    // the pole, on the sub-satellite meridian
    public void PointsOverTheHorizonHaveNoScanAngle(double latDeg, double lonDeg) =>
        Assert.Null(GoesEast().Forward(latDeg, lonDeg));

    [Theory]
    [InlineData(0.0, -75.0)]
    [InlineData(40.3, -111.9)]
    [InlineData(-20.0, -50.0)]
    [InlineData(50.0, -60.0)]
    public void ForwardAndInverseAgree(double latDeg, double lonDeg)
    {
        var projection = GoesEast();

        var scan = projection.Forward(latDeg, lonDeg);
        Assert.NotNull(scan);
        var back = projection.Inverse(scan!.Value.X, scan.Value.Y);

        Assert.NotNull(back);
        Assert.Equal(latDeg, back!.Value.LatDeg, 8);
        Assert.Equal(lonDeg, back.Value.LonDeg, 8);
    }

    /// <summary>
    /// The sub-satellite point is the one place the geometry is trivially checkable: it sits
    /// at scan angle zero, and the distance to it is the perspective point height exactly.
    /// </summary>
    [Fact]
    public void TheSubSatellitePointIsTheOriginOfTheScanAngles()
    {
        var scan = Geostationary.GoesR(-137.0).Forward(0.0, -137.0);

        Assert.NotNull(scan);
        Assert.Equal(0.0, scan!.Value.X, 12);
        Assert.Equal(0.0, scan.Value.Y, 12);
    }

    /// <summary>
    /// The height the file states is above the ellipsoid; the equations need the distance
    /// from the earth's centre. Adding the equatorial radius is the difference between a
    /// satellite in orbit and one 29,000 km underground, and the failure is not subtle:
    /// latitudes come out wrong by degrees rather than by metres.
    /// </summary>
    [Fact]
    public void HeightIsMeasuredFromTheSurfaceNotTheCentre()
    {
        var correct = Geostationary.GoesR(-75.0).Forward(40.0, -100.0);
        var underground = new Geostationary(
            35786023.0 - 6378137.0, 6378137.0, 6356752.31414, -75.0).Forward(40.0, -100.0);

        Assert.NotNull(correct);
        Assert.NotNull(underground);
        Assert.True(Math.Abs(correct!.Value.Y - underground!.Value.Y) > 0.01,
            "getting the reference surface wrong should be obvious, not a rounding difference");
    }
}
