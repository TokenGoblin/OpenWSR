// Derives the mean-vs-median comparison in RotationTracks.Smooth. See docs/verification.md
// for why the smoothing's job changed once the quality mask arrived.
//
// Copy into tests/OpenWSR.Nexrad.Tests/ and run:
//   OWSR_OUT=<file> dotnet test tests/OpenWSR.Nexrad.Tests \
//       --filter "FullyQualifiedName~SmoothingMeasurement"
//
// This one measures whichever filter RotationTracks currently uses, so producing the full
// table means editing Smooth between runs -- mean, median, and returning `raw` unchanged
// for the no-filter row. Recorded result on one masked scan of the Moore volume:
//
//   filter    strong cells   peak
//   none                37   0.0616 1/s
//   mean                23   0.0519
//   median              27   0.0565

using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

public sealed class SmoothingMeasurement(DecodedVolumes volumes) : IClassFixture<DecodedVolumes>
{
    [Fact]
    public void Measure()
    {
        var doppler = volumes.Moore.Sweeps
            .Where(s => s.Moment == Moment.Velocity).MinBy(s => s.ElevationIndex)!;
        var shear = AzimuthalShear.Compute(VelocityDealiasing.Dealias(doppler));
        var reflectivity = GateQuality.ReflectivityFor(volumes.Moore.Sweeps, doppler)!;
        var masked = GateQuality.MaskByReflectivity(shear, reflectivity);

        var lines = new List<string>
        {
            "One scan, accumulated alone so the swath's own behaviour is isolated.",
            "",
            "field    cells  strong  peak     p99      p95      median",
        };

        foreach (var (label, field) in new[] { ("raw", shear), ("masked", masked) })
        {
            var swath = RotationTracks.Accumulate([field], resolution: 512);
            var values = swath.Values.Where(v => !float.IsNaN(v)).ToArray();
            int strong = values.Count(v => v > 0.005f);
            Array.Sort(values);

            lines.Add($"{label,-7} {values.Length,6}  {strong,5}  {swath.Peak:F4}   "
                      + $"{Percentile(values, 0.99):F4}   {Percentile(values, 0.95):F4}   "
                      + $"{Percentile(values, 0.50):F4}");
        }

        File.WriteAllLines(Environment.GetEnvironmentVariable("OWSR_OUT")!, lines);
    }

    private static float Percentile(float[] sorted, double p) =>
        sorted.Length == 0
            ? float.NaN
            : sorted[Math.Clamp((int)(p * sorted.Length), 0, sorted.Length - 1)];
}
