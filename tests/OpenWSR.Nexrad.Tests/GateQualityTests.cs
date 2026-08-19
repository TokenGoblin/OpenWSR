using System.Collections;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// The quality mask behind rotation tracks. A mask is only worth having if it removes noise
/// <em>and</em> leaves the signal alone, so both halves are asserted — and the second half
/// on the Moore tornado, where the signal is not in doubt.
/// </summary>
public sealed class GateQualityTests(DecodedVolumes volumes) : IClassFixture<DecodedVolumes>
{
    // ---- synthetic: the rule itself ----

    private static Sweep Grid(Moment moment, float[] data, int radials, int gates,
                              float firstGateM = 2125f, float spacingM = 250f)
    {
        var azimuths = new float[radials];
        for (int i = 0; i < radials; i++) azimuths[i] = i * 360f / radials;
        return new Sweep(
            "KTLX", new DateTime(2013, 5, 20, 20, 16, 0, DateTimeKind.Utc),
            35.3331, -97.2778, 380, 2, 0.5f, moment,
            azimuths, firstGateM, spacingM, gates, data,
            new ScaleInfo(2f, 129f, 8), new BitArray(radials * gates));
    }

    [Fact]
    public void GatesUnderWeakEchoAreBlanked()
    {
        var field = Grid(Moment.AzimuthalShear, [0.01f, 0.02f, 0.03f, 0.04f], 2, 2);
        var echo = Grid(Moment.Reflectivity, [45f, 5f, 30f, float.NaN], 2, 2);

        var masked = GateQuality.MaskByReflectivity(field, echo, minDbz: 20f);

        Assert.Equal(0.01f, masked.Data[0]);      // 45 dBZ: kept
        Assert.True(float.IsNaN(masked.Data[1])); //  5 dBZ: blanked
        Assert.Equal(0.03f, masked.Data[2]);      // 30 dBZ: kept
        Assert.True(float.IsNaN(masked.Data[3])); // no echo at all: blanked
    }

    [Fact]
    public void AGateThatWasAlreadyNoDataStaysNoData()
    {
        var field = Grid(Moment.AzimuthalShear, [float.NaN, 0.02f], 1, 2);
        var echo = Grid(Moment.Reflectivity, [60f, 60f], 1, 2);

        var masked = GateQuality.MaskByReflectivity(field, echo);

        Assert.True(float.IsNaN(masked.Data[0]));
        Assert.Equal(0.02f, masked.Data[1]);
    }

    [Fact]
    public void GatesAreMatchedBySlantRangeNotByIndex()
    {
        // The reflectivity sweep starts closer in and steps at half the spacing, so index 2
        // of the field is index 5 of the echo. Indexing one with the other's numbers would
        // read the wrong gate — wrong by kilometres, and never obviously wrong on screen.
        var field = Grid(Moment.AzimuthalShear, [0.01f, 0.02f, 0.03f], 1, 3,
            firstGateM: 1000f, spacingM: 1000f);
        var echo = Grid(Moment.Reflectivity, [0f, 0f, 0f, 0f, 0f, 50f], 1, 6,
            firstGateM: 500f, spacingM: 500f);

        // Field gate 2 sits at 3000 m; echo gate 5 sits at 3000 m and is the only strong one.
        var masked = GateQuality.MaskByReflectivity(field, echo, minDbz: 20f);

        Assert.True(float.IsNaN(masked.Data[0]));
        Assert.True(float.IsNaN(masked.Data[1]));
        Assert.Equal(0.03f, masked.Data[2]);
    }

    [Fact]
    public void GatesBeyondTheEchoSweepsRangeAreBlanked()
    {
        var field = Grid(Moment.AzimuthalShear, [0.01f, 0.02f, 0.03f], 1, 3);
        var echo = Grid(Moment.Reflectivity, [50f], 1, 1);

        var masked = GateQuality.MaskByReflectivity(field, echo);

        Assert.Equal(0.01f, masked.Data[0]);
        Assert.True(float.IsNaN(masked.Data[1]));
        Assert.True(float.IsNaN(masked.Data[2]));
    }

    [Fact]
    public void TheMaskLeavesGeometryAndMetadataAlone()
    {
        var field = Grid(Moment.AzimuthalShear, [0.01f, 0.02f], 1, 2);
        var masked = GateQuality.MaskByReflectivity(field, Grid(Moment.Reflectivity, [50f, 50f], 1, 2));

        Assert.Equal(field.Moment, masked.Moment);
        Assert.Equal(field.ElevationAngleDeg, masked.ElevationAngleDeg);
        Assert.Equal(field.AzimuthsDeg, masked.AzimuthsDeg);
        Assert.Equal(field.GateCount, masked.GateCount);
        Assert.Equal(field.ScanTimeUtc, masked.ScanTimeUtc);
    }

    // ---- pairing ----

    [Fact]
    public void TheReflectivityFromTheSameCutIsPreferred()
    {
        var doppler = volumes.Moore.Sweeps
            .Where(s => s.Moment == Moment.Velocity).MinBy(s => s.ElevationIndex)!;

        var paired = GateQuality.ReflectivityFor(volumes.Moore.Sweeps, doppler);

        Assert.NotNull(paired);
        Assert.Equal(doppler.ElevationIndex, paired!.ElevationIndex);
        // A split-cut VCP puts reflectivity on the Doppler cut, so the beams line up exactly.
        Assert.Equal(doppler.RadialCount, paired.RadialCount);
        Assert.Equal(doppler.AzimuthsDeg, paired.AzimuthsDeg);
    }

    [Fact]
    public void AVolumeWithNoReflectivityAtAllReturnsNull()
    {
        var doppler = volumes.Moore.Sweeps.First(s => s.Moment == Moment.Velocity);
        Assert.Null(GateQuality.ReflectivityFor([doppler], doppler));
    }

    // ---- the Moore volume: does it remove noise without removing the tornado? ----

    private (Sweep Shear, Sweep Reflectivity) MooreShear()
    {
        var doppler = volumes.Moore.Sweeps
            .Where(s => s.Moment == Moment.Velocity).MinBy(s => s.ElevationIndex)!;
        var shear = AzimuthalShear.Compute(VelocityDealiasing.Dealias(doppler));
        return (shear, GateQuality.ReflectivityFor(volumes.Moore.Sweeps, doppler)!);
    }

    [Fact]
    public void MostStrongShearSitsWhereThereIsNoEchoToHaveReflected()
    {
        // The measurement the mask exists for. If this ever drops a lot, the speckle has
        // gone away by some other route and the mask should be re-justified rather than
        // kept out of habit.
        var (shear, reflectivity) = MooreShear();

        int strong = 0, strongWeakEcho = 0;
        for (int i = 0; i < shear.Data.Length; i++)
        {
            if (float.IsNaN(shear.Data[i]) || shear.Data[i] <= 0.005f) continue;
            strong++;
            float dbz = reflectivity.Data[i];
            if (float.IsNaN(dbz) || dbz < GateQuality.DefaultMinReflectivityDbz) strongWeakEcho++;
        }

        Assert.True(strong > 1000, $"only {strong} strong-shear gates to judge by");
        Assert.True(strongWeakEcho / (double)strong > 0.8,
            $"only {strongWeakEcho / (double)strong:P1} of strong shear was under weak echo");
    }

    [Fact]
    public void TheMaskRemovesMostOfTheSpeckle()
    {
        var (shear, reflectivity) = MooreShear();
        var masked = GateQuality.MaskByReflectivity(shear, reflectivity);

        int before = shear.Data.Count(v => !float.IsNaN(v) && v > 0.005f);
        int after = masked.Data.Count(v => !float.IsNaN(v) && v > 0.005f);

        Assert.True(after < before * 0.2,
            $"strong-shear gates went {before} -> {after}, which is not much of a clean-up");
    }

    [Fact]
    public void TheMaskDoesNotTouchThePeakRotation()
    {
        // The test that matters. The Moore EF5 is the strongest rotation in this volume; a
        // mask that also removed it would look like an improvement in every summary
        // statistic and be worthless.
        var (shear, reflectivity) = MooreShear();
        var masked = GateQuality.MaskByReflectivity(shear, reflectivity);

        float before = shear.Data.Where(v => !float.IsNaN(v)).Max();
        float after = masked.Data.Where(v => !float.IsNaN(v)).Max();

        Assert.Equal(before, after, 6);
        Assert.True(after > 0.1f, $"peak rotation {after:F4} 1/s is lower than the Moore couplet");

        // And it is still in the same place, not merely the same number somewhere else.
        Assert.Equal(
            Array.IndexOf(shear.Data, before),
            Array.IndexOf(masked.Data, after));
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(10f)]
    [InlineData(15f)]
    [InlineData(20f)]
    [InlineData(25f)]
    public void ThePeakSurvivesEveryReasonableThreshold(float minDbz)
    {
        // The default is not a cliff edge: nothing interesting happens to the signal
        // anywhere in this range, which is why 20 dBZ could be chosen for how much noise it
        // removes rather than for how little signal it costs.
        var (shear, reflectivity) = MooreShear();
        float before = shear.Data.Where(v => !float.IsNaN(v)).Max();

        var masked = GateQuality.MaskByReflectivity(shear, reflectivity, minDbz);

        Assert.Equal(before, masked.Data.Where(v => !float.IsNaN(v)).Max(), 6);
    }
}
