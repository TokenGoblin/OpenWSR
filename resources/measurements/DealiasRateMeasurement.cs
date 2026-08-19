// Derives the walk-vs-merge-vs-Py-ART correction-rate table in docs/verification.md.
//
// Copy into tests/OpenWSR.Nexrad.Tests/ and run:
//   OWSR_OUT=<file> dotnet test tests/OpenWSR.Nexrad.Tests \
//       --filter "FullyQualifiedName~DealiasRateMeasurement"
//
// Py-ART's side of the table comes from resources/crosscheck/dealias_pyart.py, not from
// here. The reference figures on the Moore volume are:
//   elev 2 (0.5 deg): corrected 1393 of 155912 valid (0.89 %)
//   elev 6 (1.3 deg): corrected 4116 of 151743 valid (2.71 %)

using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

public sealed class DealiasRateMeasurement(DecodedVolumes volumes) : IClassFixture<DecodedVolumes>
{
    [Fact]
    public void Measure()
    {
        var lines = new List<string>
        {
            "KTLX 2013-05-20 20:16Z, reflectivity of the correction, per elevation cut",
            "",
        };

        var cuts = volumes.Moore.Sweeps
            .Where(s => s.Moment == Moment.Velocity)
            .OrderBy(s => s.ElevationIndex)
            .ToList();

        foreach (int elevationIndex in new[] { 2, 6 })
        {
            var cut = cuts.First(s => s.ElevationIndex == elevationIndex);
            var result = VelocityDealiasing.Dealias(cut);
            float interval = cut.AliasingIntervalMs!.Value;

            int valid = 0, corrected = 0, maxShift = 0;
            float peak = 0;
            for (int i = 0; i < cut.Data.Length; i++)
            {
                if (float.IsNaN(cut.Data[i])) continue;
                valid++;
                float delta = result.Data[i] - cut.Data[i];
                if (Math.Abs(delta) > 0.01f) corrected++;
                maxShift = Math.Max(maxShift, (int)Math.Round(Math.Abs(delta) / interval));
                peak = Math.Max(peak, Math.Abs(result.Data[i]));
            }

            lines.Add(
                $"elev {elevationIndex} ({cut.ElevationAngleDeg:F1} deg): "
                + $"valid={valid} corrected={corrected} ({corrected / (double)valid:P2}) "
                + $"maxShift={maxShift} peak={peak:F1} m/s "
                + $"nyquist={cut.NyquistMs:F1} interval={interval:F1}");
        }

        File.WriteAllLines(Environment.GetEnvironmentVariable("OWSR_OUT")!, lines);
    }
}
