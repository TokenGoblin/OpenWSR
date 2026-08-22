using System.Collections;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// The azimuth-to-radial lookup every gridded product resolves through.
///
/// It answered with a floor rather than a nearest for a while, and nothing caught it: the
/// error is under one radial spacing, so no decode changes and no golden file moves. What
/// it did was bias the volume grid, the reflectivity mask and the rotation-track swath the
/// same way at once — a quarter of a degree of consistent rotation, 650 m at 150 km. These
/// assertions fail on that behaviour.
/// </summary>
public sealed class AzimuthIndexTests
{
    /// <summary>A sweep carrying only the azimuths; no product reads the data here.</summary>
    private static Sweep At(params float[] azimuthsDeg) =>
        new("KTLX", new DateTime(2013, 5, 20, 20, 16, 0, DateTimeKind.Utc),
            35.3331, -97.2778, 380, 2, 0.5f, Moment.Reflectivity,
            azimuthsDeg, 2125f, 250f, 1, new float[azimuthsDeg.Length],
            new ScaleInfo(2f, 129f, 8), new BitArray(azimuthsDeg.Length));

    [Fact]
    public void AnAzimuthBetweenTwoRadialsTakesTheCloserOne()
    {
        var index = AzimuthIndex.Build(At(10f, 20f));

        Assert.Equal(0, AzimuthIndex.RadialFor(index, 11f));   // 1° above 10
        Assert.Equal(0, AzimuthIndex.RadialFor(index, 14.9f));
        Assert.Equal(1, AzimuthIndex.RadialFor(index, 15.1f)); // past halfway
        Assert.Equal(1, AzimuthIndex.RadialFor(index, 19f));
    }

    [Fact]
    public void TheNearestRadialIsFoundAcrossNorth()
    {
        // 359° and 5°: the wrap has to be a candidate in both directions, not just one.
        var index = AzimuthIndex.Build(At(359f, 5f));

        Assert.Equal(0, AzimuthIndex.RadialFor(index, 0.5f));  // 1.5° from 359, 4.5° from 5
        Assert.Equal(1, AzimuthIndex.RadialFor(index, 3.0f));  // 4.0° from 359, 2.0° from 5
        Assert.Equal(0, AzimuthIndex.RadialFor(index, 357f));
    }

    [Fact]
    public void ARadialsOwnAzimuthResolvesToItself()
    {
        var azimuths = new float[720];
        for (int i = 0; i < azimuths.Length; i++) azimuths[i] = i * 0.5f;
        var index = AzimuthIndex.Build(At(azimuths));

        for (int i = 0; i < azimuths.Length; i++)
            Assert.Equal(i, AzimuthIndex.RadialFor(index, azimuths[i]));
    }

    [Fact]
    public void EveryBinResolvesEvenFromASingleRadial()
    {
        var index = AzimuthIndex.Build(At(123.4f));

        Assert.All(index, radial => Assert.Equal(0, radial));
    }

    [Fact]
    public void ASweepWithNoRadialsResolvesToNothing()
    {
        var index = AzimuthIndex.Build(At());

        Assert.All(index, radial => Assert.Equal(-1, radial));
    }
}
