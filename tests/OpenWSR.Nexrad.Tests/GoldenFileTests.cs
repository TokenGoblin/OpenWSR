namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Golden-file tests against the committed test volumes. All expected values were
/// produced by MetPy 1.7.1 (an independent decoder) — see the build plan §5 Testing.
/// </summary>
public sealed class DecodedVolumes
{
    public RadarVolume Moore { get; } = ArchiveFile.DecodeFile(TestData.Path("KTLX20130520_201643_V06.gz"));
    public RadarVolume Quiet { get; } = ArchiveFile.DecodeFile(TestData.Path("KTLX20260810_181228_V06"));
}

public static class TestData
{
    public static string Path(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "OpenWSR.slnx")))
            dir = dir.Parent!;
        if (dir is null) throw new InvalidOperationException("Repo root not found.");
        return System.IO.Path.Combine(dir.FullName, "assets", "testdata", name);
    }
}

public class GoldenFileTests(DecodedVolumes volumes) : IClassFixture<DecodedVolumes>
{
    private static Sweep GetSweep(RadarVolume v, int elevationIndex, Moment moment) =>
        v.Sweeps.Single(s => s.ElevationIndex == elevationIndex && s.Moment == moment);

    // ---- Moore, OK tornado volume: KTLX 2013-05-20 20:16Z, VCP 12, gzip era ----

    [Fact]
    public void Moore_VolumeMetadata()
    {
        var v = volumes.Moore;
        Assert.Equal("KTLX", v.SiteId);
        Assert.Equal(35.33305740356445, v.LatDeg, 12);
        Assert.Equal(-97.27748107910156, v.LonDeg, 12);
        Assert.Equal(388.0, v.AltitudeM);
        Assert.Equal(12, v.VcpNumber);
        // StartTimeUtc is the first radial's collection time (20:16:43); the volume
        // header carries a later write stamp (20:16:46, what MetPy reports as dt).
        Assert.Equal(new DateTime(2013, 5, 20, 20, 16, 43, DateTimeKind.Utc),
            new DateTime(v.StartTimeUtc.Year, v.StartTimeUtc.Month, v.StartTimeUtc.Day,
                v.StartTimeUtc.Hour, v.StartTimeUtc.Minute, v.StartTimeUtc.Second, DateTimeKind.Utc));
    }

    [Fact]
    public void Moore_LowestReflectivitySweepGeometry()
    {
        var s = GetSweep(volumes.Moore, 1, Moment.Reflectivity);
        Assert.Equal(720, s.RadialCount);
        Assert.Equal(1832, s.GateCount);
        Assert.Equal(2125f, s.FirstGateM);
        Assert.Equal(250f, s.GateSpacingM);
        Assert.Equal(2.0f, s.Scale.Scale);
        Assert.Equal(66.0f, s.Scale.Offset);
        Assert.Equal(8, s.Scale.WordSizeBits);
    }

    [Fact]
    public void Moore_Radial100_MatchesMetPy()
    {
        // MetPy ray index 100 (az_num 101): az=173.2049560546875
        var refl = GetSweep(volumes.Moore, 1, Moment.Reflectivity);
        Assert.Equal(173.2049560546875f, refl.AzimuthsDeg[100]);
        Assert.Equal(-7.5f, refl.Data[100 * refl.GateCount + 0]);
        Assert.Equal(-5.5f, refl.Data[100 * refl.GateCount + 1]);
        Assert.Equal(-5.5f, refl.Data[100 * refl.GateCount + 2]);
        // Gates 400..407 on this radial are below threshold in the reference decoder.
        for (int g = 400; g < 408; g++)
            Assert.True(float.IsNaN(refl.Data[100 * refl.GateCount + g]));

        var zdr = GetSweep(volumes.Moore, 1, Moment.DifferentialReflectivity);
        Assert.Equal(7.9375f, zdr.Data[100 * zdr.GateCount + 0]);
        Assert.Equal(2.6875f, zdr.Data[100 * zdr.GateCount + 1]);
        Assert.Equal(1.4375f, zdr.Data[100 * zdr.GateCount + 2]);

        var phi = GetSweep(volumes.Moore, 1, Moment.DifferentialPhase);
        Assert.Equal(16, phi.Scale.WordSizeBits);
        Assert.Equal(354.712444, phi.Data[100 * phi.GateCount + 0], 3);
        Assert.Equal(44.074607, phi.Data[100 * phi.GateCount + 1], 3);
        Assert.Equal(42.311623, phi.Data[100 * phi.GateCount + 2], 3);

        var rho = GetSweep(volumes.Moore, 1, Moment.CorrelationCoefficient);
        Assert.Equal(0.748333, rho.Data[100 * rho.GateCount + 0], 4);
        Assert.Equal(0.885, rho.Data[100 * rho.GateCount + 1], 4);
        Assert.Equal(0.745, rho.Data[100 * rho.GateCount + 2], 4);
    }

    [Fact]
    public void Moore_PhysicallySaneReflectivity()
    {
        foreach (var s in volumes.Moore.Sweeps.Where(s => s.Moment == Moment.Reflectivity))
            foreach (var v in s.Data)
                if (!float.IsNaN(v))
                    Assert.InRange(v, -35f, 95f);
    }

    // ---- Quiet-weather volume: KTLX 2026-08-10 18:12Z, VCP 35, modern format ----

    [Fact]
    public void Quiet_VolumeMetadata()
    {
        var v = volumes.Quiet;
        Assert.Equal("KTLX", v.SiteId);
        Assert.Equal(35.3333625793457, v.LatDeg, 12);
        Assert.Equal(-97.27776336669922, v.LonDeg, 12);
        Assert.Equal(389.0, v.AltitudeM);
        Assert.Equal(35, v.VcpNumber);
    }

    [Fact]
    public void Quiet_Radial0_MatchesMetPy()
    {
        var refl = GetSweep(volumes.Quiet, 1, Moment.Reflectivity);
        Assert.Equal(322.25372314453125f, refl.AzimuthsDeg[0]);
        Assert.Equal(0.52734375f, refl.ElevationAngleDeg);
        Assert.True(float.IsNaN(refl.Data[0]));
        Assert.Equal(5.0f, refl.Data[1]);
        Assert.Equal(-5.5f, refl.Data[2]);
        Assert.Equal(-14.0f, refl.Data[3]);
        Assert.Equal(-5.0f, refl.Data[4]);
        Assert.Equal(-1.5f, refl.Data[5]);

        // ZDR in this volume is 16-bit — exercises the wide-word moment path.
        var zdr = GetSweep(volumes.Quiet, 1, Moment.DifferentialReflectivity);
        Assert.Equal(16, zdr.Scale.WordSizeBits);
        Assert.True(float.IsNaN(zdr.Data[0]));
        Assert.Equal(3.21875f, zdr.Data[1]);
        Assert.Equal(1.90625f, zdr.Data[2]);
        Assert.Equal(3.90625f, zdr.Data[3]);

        var cfp = GetSweep(volumes.Quiet, 1, Moment.ClutterFilterPower);
        Assert.Equal(73.0f, cfp.Data[0]);
        Assert.Equal(25.0f, cfp.Data[1]);
        Assert.Equal(38.0f, cfp.Data[2]);

        var rho = GetSweep(volumes.Quiet, 1, Moment.CorrelationCoefficient);
        Assert.Equal(0.971667, rho.Data[1], 4);
        Assert.Equal(0.808333, rho.Data[2], 4);
    }

    [Fact]
    public void Quiet_VcpDefinitionDecoded()
    {
        var vcp = volumes.Quiet.Vcp;
        Assert.NotNull(vcp);
        Assert.Equal(35, vcp.PatternNumber);
        Assert.True(vcp.Cuts.Count >= 7);
        // The nominal "0.5 deg" cut is coded as 0.4833984375 in binary angle units;
        // MetPy decodes the identical value.
        Assert.Equal(0.4833984375f, vcp.Cuts[0].ElevationDeg);
    }

    [Fact]
    public void Quiet_SplitCutsShareElevationButDifferInMoments()
    {
        // Cut 1 is the surveillance pass (REF, no VEL); cut 2 the Doppler pass (VEL present).
        var v = volumes.Quiet;
        Assert.DoesNotContain(v.Sweeps, s => s.ElevationIndex == 1 && s.Moment == Moment.Velocity);
        Assert.Contains(v.Sweeps, s => s.ElevationIndex == 2 && s.Moment == Moment.Velocity);
    }
}
