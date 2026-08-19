// Derives the reflectivity-threshold sweep behind GateQuality.DefaultMinReflectivityDbz,
// the "88.8 % of strong shear sits under weak echo" figure, and the correlation-coefficient
// numbers that argue against adding a CC mask. See docs/verification.md.
//
// Copy into tests/OpenWSR.Nexrad.Tests/ and run:
//   OWSR_OUT=<file> dotnet test tests/OpenWSR.Nexrad.Tests \
//       --filter "FullyQualifiedName~ShearQualityMeasurement"
//
// The result that matters is that the peak does not move at any threshold: a mask which
// also removed the tornado would improve every summary statistic and be worthless.

using OpenWSR.Geo;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

public sealed class ShearQualityMeasurement(DecodedVolumes volumes) : IClassFixture<DecodedVolumes>
{
    /// <summary>Cyclonic shear at or above this counts as "strong" for these counts.</summary>
    private const float StrongShear = 0.005f;

    [Fact]
    public void Measure()
    {
        var lines = new List<string>();
        var volume = volumes.Moore;

        var doppler = volume.Sweeps
            .Where(s => s.Moment == Moment.Velocity)
            .MinBy(s => s.ElevationIndex)!;
        var reflectivity = GateQuality.ReflectivityFor(volume.Sweeps, doppler)!;
        var cc = volume.Sweeps
            .Where(s => s.Moment == Moment.CorrelationCoefficient)
            .MinBy(s => Math.Abs(s.ElevationAngleDeg - doppler.ElevationAngleDeg))!;

        lines.Add($"doppler idx={doppler.ElevationIndex} {doppler.ElevationAngleDeg:F2} deg "
                  + $"radials={doppler.RadialCount} gates={doppler.GateCount}");
        lines.Add($"refl    idx={reflectivity.ElevationIndex} gates={reflectivity.GateCount} "
                  + $"sameAzimuths={SameAzimuths(doppler, reflectivity)}");
        lines.Add($"cc      idx={cc.ElevationIndex} {cc.ElevationAngleDeg:F2} deg "
                  + $"gates={cc.GateCount} sameAzimuths={SameAzimuths(doppler, cc)}");
        lines.Add("");

        var shear = AzimuthalShear.Compute(VelocityDealiasing.Dealias(doppler));
        var ccIndex = AzimuthIndex.Build(cc);

        // How much of the strong shear is under weak echo -- the case for the mask.
        int strong = 0, strongWeakEcho = 0, strongLowCc = 0;
        for (int r = 0; r < shear.RadialCount; r++)
            for (int g = 0; g < shear.GateCount; g++)
            {
                float s = shear.Data[r * shear.GateCount + g];
                if (float.IsNaN(s) || s <= StrongShear) continue;
                strong++;

                float dbz = reflectivity.Data[r * reflectivity.GateCount + g];
                if (float.IsNaN(dbz) || dbz < 20f) strongWeakEcho++;

                float rho = SampleCc(cc, ccIndex, shear.AzimuthsDeg[r], g);
                if (float.IsNaN(rho) || rho < 0.85f) strongLowCc++;
            }

        lines.Add($"strong shear (> {StrongShear} 1/s): {strong} gates");
        lines.Add($"  under < 20 dBZ or no echo: {strongWeakEcho} ({strongWeakEcho / (double)strong:P1})");
        lines.Add($"  under CC < 0.85 or none:   {strongLowCc} ({strongLowCc / (double)strong:P1})");
        lines.Add("");

        // The threshold sweep. The peak column is the point of the whole exercise.
        lines.Add("threshold  kept    strong  peak       location");
        foreach (float minDbz in new[] { float.NegativeInfinity, 5f, 10f, 15f, 20f, 25f })
            lines.Add(Sweep(shear, reflectivity, minDbz));
        lines.Add("");

        // What a CC mask would cost: these are debris-signature gates.
        int debris = 0, debrisLowCc = 0;
        for (int r = 0; r < shear.RadialCount; r++)
            for (int g = 0; g < shear.GateCount; g++)
            {
                float s = shear.Data[r * shear.GateCount + g];
                float dbz = reflectivity.Data[r * reflectivity.GateCount + g];
                if (float.IsNaN(s) || float.IsNaN(dbz) || s <= StrongShear || dbz < 40f) continue;
                debris++;
                float rho = SampleCc(cc, ccIndex, shear.AzimuthsDeg[r], g);
                if (float.IsNaN(rho) || rho < 0.85f) debrisLowCc++;
            }

        lines.Add($"strong shear under >= 40 dBZ: {debris} gates, "
                  + $"of which CC < 0.85: {debrisLowCc} "
                  + $"({(debris == 0 ? 0 : debrisLowCc / (double)debris):P1})");
        lines.Add("  -- a CC threshold would delete these, and they are the debris signature.");

        File.WriteAllLines(Environment.GetEnvironmentVariable("OWSR_OUT")!, lines);
    }

    private static string Sweep(Sweep shear, Sweep reflectivity, float minDbz)
    {
        int kept = 0, strong = 0;
        float peak = 0;
        double peakLat = 0, peakLon = 0;

        for (int r = 0; r < shear.RadialCount; r++)
            for (int g = 0; g < shear.GateCount; g++)
            {
                float s = shear.Data[r * shear.GateCount + g];
                if (float.IsNaN(s)) continue;
                float dbz = reflectivity.Data[r * reflectivity.GateCount + g];
                if (float.IsNaN(dbz) || dbz < minDbz) continue;

                kept++;
                if (s > StrongShear) strong++;
                if (s <= peak) continue;

                peak = s;
                double slant = shear.FirstGateM + g * shear.GateSpacingM;
                var (ground, _) = GeoMath.BeamPath(slant, shear.ElevationAngleDeg * Math.PI / 180.0);
                (peakLat, peakLon) = GeoMath.Offset(
                    shear.RadarLatDeg, shear.RadarLonDeg,
                    shear.AzimuthsDeg[r] * Math.PI / 180.0, ground);
            }

        string label = float.IsNegativeInfinity(minDbz) ? "none" : $"{minDbz:F0} dBZ";
        return $"{label,-9}  {kept,6}  {strong,5}  {peak:F4}     {peakLat:F3},{peakLon:F3}";
    }

    private static bool SameAzimuths(Sweep a, Sweep b)
    {
        if (a.RadialCount != b.RadialCount) return false;
        for (int i = 0; i < a.RadialCount; i++)
            if (Math.Abs(a.AzimuthsDeg[i] - b.AzimuthsDeg[i]) > 0.05f) return false;
        return true;
    }

    private static float SampleCc(Sweep cc, int[] index, float azimuthDeg, int gate)
    {
        if (gate >= cc.GateCount) return float.NaN;
        int radial = AzimuthIndex.RadialFor(index, azimuthDeg);
        return radial < 0 ? float.NaN : cc.Data[radial * cc.GateCount + gate];
    }
}
