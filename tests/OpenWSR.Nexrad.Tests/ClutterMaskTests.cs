using OpenWSR.Geo;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// The clutter mask has to satisfy two things at once that pull against each other: remove
/// the speckle a clear-air coastal volume fills the rotation-track swath with, and leave a
/// tornadic debris signature untouched. Both are low-CC targets, so a bare CC threshold
/// fails the second — and fails it silently, by finding a different storm feature.
///
/// Numbers are from <c>resources/measurements/ClutterDiscriminationMeasurement.cs</c>, run
/// through the same public API these tests call.
/// </summary>
public sealed class ClutterMaskTests(DecodedVolumes volumes) : IClassFixture<DecodedVolumes>
{
    private const float StrongShear = 0.005f;

    private static (Sweep Shear, Sweep Reflectivity, Sweep Cc) Prepare(RadarVolume volume)
    {
        var doppler = volume.Sweeps
            .Where(s => s.Moment == Moment.Velocity).MinBy(s => s.ElevationIndex)!;
        var reflectivity = GateQuality.ReflectivityFor(volume.Sweeps, doppler)!;
        var cc = GateQuality.CorrelationFor(volume.Sweeps, doppler)!;
        var shear = GateQuality.MaskByReflectivity(
            AzimuthalShear.Compute(VelocityDealiasing.Dealias(doppler)), reflectivity);
        return (shear, reflectivity, cc);
    }

    private static RadarVolume Coastal() =>
        ArchiveFile.DecodeFile(TestData.Path("clutter/KBOX20260720_090022_V06"));

    // ---- What it must not touch: the Moore debris signature ----

    [Fact]
    public void TheMaskLeavesTheTornadoPeakExactlyWhereItWas()
    {
        var (shear, reflectivity, cc) = Prepare(volumes.Moore);
        var before = Peak(shear);
        var after = Peak(GateQuality.MaskClutter(shear, reflectivity, cc));

        // Bit-identical, not merely close: a mask that moved the peak would be finding a
        // different feature and calling it the same one.
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.LatDeg, after.LatDeg, 6);
        Assert.Equal(before.LonDeg, after.LonDeg, 6);
        Assert.Equal(0.1297f, after.Value, 4);
    }

    [Fact]
    public void TheMaskKeepsEveryDebrisGate()
    {
        var (shear, reflectivity, cc) = Prepare(volumes.Moore);
        int before = DebrisGates(shear, reflectivity, cc);
        int after = DebrisGates(GateQuality.MaskClutter(shear, reflectivity, cc), reflectivity, cc);

        Assert.Equal(89, before);
        Assert.Equal(89, after);
    }

    [Fact]
    public void ABareCorrelationThresholdWouldDestroyTheSignature()
    {
        // The dead end this design exists to avoid, kept as a test so it stays ruled out.
        // Removing the reflectivity ceiling deletes all 89 debris gates and moves the peak
        // from the tornado at 35.323,-97.527 to an unrelated feature 20 km east.
        var (shear, reflectivity, cc) = Prepare(volumes.Moore);
        var bare = GateQuality.MaskClutter(
            shear, reflectivity, cc, maxDbz: float.PositiveInfinity);

        Assert.Equal(0, DebrisGates(bare, reflectivity, cc));
        var peak = Peak(bare);
        Assert.Equal(0.1000f, peak.Value, 4);
        Assert.True(GeoMath.DistanceM(peak.LatDeg, peak.LonDeg, 35.323, -97.527) > 15_000,
            "the bare-CC peak should land on a different feature entirely");
    }

    // ---- What it must remove: clear-air coastal clutter ----

    [Fact]
    public void TheMaskRemovesMostCoastalSpeckle()
    {
        var (shear, reflectivity, cc) = Prepare(Coastal());
        int before = StrongGates(shear);
        int after = StrongGates(GateQuality.MaskClutter(shear, reflectivity, cc));

        Assert.Equal(23, before);
        Assert.Equal(3, after);
    }

    [Fact]
    public void CoastalClutterNoLongerReadsAsRotation()
    {
        // 0.0995 1/s is squarely in the range a real mesocyclone produces, and on this
        // volume — a clear July night with no convection anywhere near the radar — every
        // bit of it was sea clutter.
        var (shear, reflectivity, cc) = Prepare(Coastal());
        Assert.Equal(0.0995f, Peak(shear).Value, 4);
        Assert.Equal(0.0226f, Peak(GateQuality.MaskClutter(shear, reflectivity, cc)).Value, 4);
    }

    [Fact]
    public void TheCombinedMaskIsExactlyTheTwoStepsInSequence()
    {
        // Mask() exists to resample reflectivity once instead of twice. It is only worth
        // having if it changes nothing, so this compares gate for gate rather than
        // comparing summary numbers that could agree by luck.
        var volume = volumes.Moore;
        var doppler = volume.Sweeps
            .Where(s => s.Moment == Moment.Velocity).MinBy(s => s.ElevationIndex)!;
        var reflectivity = GateQuality.ReflectivityFor(volume.Sweeps, doppler)!;
        var cc = GateQuality.CorrelationFor(volume.Sweeps, doppler)!;
        var raw = AzimuthalShear.Compute(VelocityDealiasing.Dealias(doppler));

        var stepwise = GateQuality.MaskClutter(
            GateQuality.MaskByReflectivity(raw, reflectivity), reflectivity, cc);
        var combined = GateQuality.Mask(raw, reflectivity, cc);

        Assert.Equal(stepwise.Data.Length, combined.Data.Length);
        for (int i = 0; i < stepwise.Data.Length; i++)
        {
            if (float.IsNaN(stepwise.Data[i]))
                Assert.True(float.IsNaN(combined.Data[i]), $"gate {i} should be blank");
            else
                Assert.Equal(stepwise.Data[i], combined.Data[i]);
        }
    }

    [Fact]
    public void WithNoCorrelationSweepItIsJustTheEchoMask()
    {
        // A volume with no low-level dual-pol gets the reflectivity mask and nothing else,
        // rather than an exception or an unmasked field.
        var volume = volumes.Moore;
        var doppler = volume.Sweeps
            .Where(s => s.Moment == Moment.Velocity).MinBy(s => s.ElevationIndex)!;
        var reflectivity = GateQuality.ReflectivityFor(volume.Sweeps, doppler)!;
        var raw = AzimuthalShear.Compute(VelocityDealiasing.Dealias(doppler));

        var echoOnly = GateQuality.MaskByReflectivity(raw, reflectivity);
        var combined = GateQuality.Mask(raw, reflectivity, correlation: null);

        for (int i = 0; i < echoOnly.Data.Length; i++)
            if (float.IsNaN(echoOnly.Data[i]))
                Assert.True(float.IsNaN(combined.Data[i]));
            else
                Assert.Equal(echoOnly.Data[i], combined.Data[i]);
    }

    // ---- The pairing rule ----

    [Fact]
    public void CorrelationIsTakenFromTheSplitCutPartnerNotTheSameIndex()
    {
        // VCP 12 splits each low cut: velocity on one, CC on the other, at slightly
        // different angles and different elevation indices. Matching by index would pair
        // the wrong altitude, and matching by angle is what makes it right.
        var volume = volumes.Moore;
        var doppler = volume.Sweeps
            .Where(s => s.Moment == Moment.Velocity).MinBy(s => s.ElevationIndex)!;
        var cc = GateQuality.CorrelationFor(volume.Sweeps, doppler)!;

        Assert.NotEqual(doppler.ElevationIndex, cc.ElevationIndex);
        Assert.True(Math.Abs(cc.ElevationAngleDeg - doppler.ElevationAngleDeg) < 0.5);
    }

    [Fact]
    public void NoCorrelationNearTheCutMeansNoMask()
    {
        // A volume whose only CC sits degrees away must return null rather than mask
        // against the wrong altitude — leaving the field unmasked is the safe way to be wrong.
        var volume = volumes.Moore;
        var doppler = volume.Sweeps
            .Where(s => s.Moment == Moment.Velocity).MinBy(s => s.ElevationIndex)!;
        var distant = volume.Sweeps
            .Where(s => s.Moment == Moment.CorrelationCoefficient)
            .OrderByDescending(s => s.ElevationAngleDeg)
            .Take(1)
            .ToList();

        Assert.True(distant[0].ElevationAngleDeg - doppler.ElevationAngleDeg > 0.5);
        Assert.Null(GateQuality.CorrelationFor(distant, doppler));
    }

    // ---- helpers ----

    private static int StrongGates(Sweep shear)
    {
        int n = 0;
        foreach (float s in shear.Data)
            if (!float.IsNaN(s) && s > StrongShear) n++;
        return n;
    }

    private static int DebrisGates(Sweep shear, Sweep reflectivity, Sweep cc)
    {
        var index = AzimuthIndex.Build(cc);
        int n = 0;
        for (int r = 0; r < shear.RadialCount; r++)
        {
            int ccRadial = AzimuthIndex.RadialFor(index, shear.AzimuthsDeg[r]);
            if (ccRadial < 0) continue;
            for (int g = 0; g < shear.GateCount; g++)
            {
                float s = shear.Data[r * shear.GateCount + g];
                if (float.IsNaN(s) || s <= StrongShear) continue;
                if (g >= reflectivity.GateCount || g >= cc.GateCount) continue;

                float dbz = reflectivity.Data[r * reflectivity.GateCount + g];
                float rho = cc.Data[ccRadial * cc.GateCount + g];
                if (!float.IsNaN(dbz) && dbz >= 40f && !float.IsNaN(rho) && rho < 0.85f) n++;
            }
        }
        return n;
    }

    private static (float Value, double LatDeg, double LonDeg) Peak(Sweep shear)
    {
        float peak = 0;
        double lat = 0, lon = 0;
        for (int r = 0; r < shear.RadialCount; r++)
            for (int g = 0; g < shear.GateCount; g++)
            {
                float s = shear.Data[r * shear.GateCount + g];
                if (float.IsNaN(s) || s <= peak) continue;
                peak = s;
                double slant = shear.FirstGateM + g * shear.GateSpacingM;
                var (ground, _) = GeoMath.BeamPath(slant, shear.ElevationAngleDeg * Math.PI / 180.0);
                (lat, lon) = GeoMath.Offset(
                    shear.RadarLatDeg, shear.RadarLonDeg,
                    shear.AzimuthsDeg[r] * Math.PI / 180.0, ground);
            }
        return (peak, lat, lon);
    }
}
