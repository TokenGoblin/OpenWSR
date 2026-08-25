// Derives the clutter-mask design behind GateQuality.MaskClutter: which field separates
// ground clutter from tornado debris when both are low-CC high-Z targets, and where the
// threshold goes. Run through the production API so the numbers quoted in the tests and in
// docs/verification.md are the ones the app actually produces.
//
//   OWSR_OUT=<file> dotnet test tests/OpenWSR.Nexrad.Tests \
//       --filter "FullyQualifiedName~ClutterDiscriminationMeasurement"

using OpenWSR.Geo;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

public sealed class ClutterDiscriminationMeasurement(DecodedVolumes volumes)
    : IClassFixture<DecodedVolumes>
{
    private const float StrongShear = 0.005f;

    [Fact]
    public void Measure()
    {
        var lines = new List<string>();

        foreach (var (label, volume) in new[]
                 {
                     ("KTLX 2013-05-20 20:16Z (Moore tornado)", volumes.Moore),
                     ("KBOX 2026-07-20 09:00Z (clear-air coast)",
                         ArchiveFile.DecodeFile(TestData.Path("clutter/KBOX20260720_090022_V06"))),
                 })
        {
            var doppler = volume.Sweeps
                .Where(s => s.Moment == Moment.Velocity).MinBy(s => s.ElevationIndex)!;
            var reflectivity = GateQuality.ReflectivityFor(volume.Sweeps, doppler)!;
            var cc = GateQuality.CorrelationFor(volume.Sweeps, doppler)!;
            var raw = AzimuthalShear.Compute(VelocityDealiasing.Dealias(doppler));

            var echoMasked = GateQuality.MaskByReflectivity(raw, reflectivity);
            var clutterMasked = GateQuality.MaskClutter(echoMasked, reflectivity, cc);

            lines.Add(label);
            lines.Add($"  doppler {doppler.ElevationAngleDeg:F2} deg idx={doppler.ElevationIndex}, "
                      + $"cc {cc.ElevationAngleDeg:F2} deg idx={cc.ElevationIndex}");
            lines.Add("  stage             gates  strong  debris  peak       location");
            lines.Add("  " + Row("raw", raw, reflectivity, cc));
            lines.Add("  " + Row("+ echo >= 20 dBZ", echoMasked, reflectivity, cc));
            lines.Add("  " + Row("+ clutter mask", clutterMasked, reflectivity, cc));
            lines.Add("");
        }

        File.WriteAllLines(Environment.GetEnvironmentVariable("OWSR_OUT")!, lines);
    }

    private static string Row(string label, Sweep shear, Sweep reflectivity, Sweep cc)
    {
        var ccIndex = AzimuthIndex.Build(cc);
        int gates = 0, strong = 0, debris = 0;
        float peak = 0;
        double peakLat = 0, peakLon = 0;

        for (int r = 0; r < shear.RadialCount; r++)
            for (int g = 0; g < shear.GateCount; g++)
            {
                float s = shear.Data[r * shear.GateCount + g];
                if (float.IsNaN(s)) continue;
                gates++;
                if (s > StrongShear)
                {
                    strong++;
                    float dbz = g < reflectivity.GateCount
                        ? reflectivity.Data[r * reflectivity.GateCount + g]
                        : float.NaN;
                    int ccRadial = AzimuthIndex.RadialFor(ccIndex, shear.AzimuthsDeg[r]);
                    float rho = ccRadial < 0 || g >= cc.GateCount
                        ? float.NaN
                        : cc.Data[ccRadial * cc.GateCount + g];
                    if (!float.IsNaN(dbz) && dbz >= 40f && !float.IsNaN(rho) && rho < 0.85f)
                        debris++;
                }
                if (s <= peak) continue;

                peak = s;
                double slant = shear.FirstGateM + g * shear.GateSpacingM;
                var (ground, _) = GeoMath.BeamPath(slant, shear.ElevationAngleDeg * Math.PI / 180.0);
                (peakLat, peakLon) = GeoMath.Offset(
                    shear.RadarLatDeg, shear.RadarLonDeg,
                    shear.AzimuthsDeg[r] * Math.PI / 180.0, ground);
            }

        return $"{label,-16}  {gates,6}  {strong,6}  {debris,6}  {peak:F4}     "
             + $"{peakLat:F3},{peakLon:F3}";
    }
}
