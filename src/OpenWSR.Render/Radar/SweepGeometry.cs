using OpenWSR.Nexrad;

namespace OpenWSR.Render.Radar;

/// <summary>
/// CPU-side prep for a sweep draw: radials sorted by azimuth, wedge edge angles, and
/// the data grid reordered to match (row i = i-th radial by azimuth). Sentinels replace
/// NaN so the shader can branch without relying on NaN semantics: below-threshold gates
/// become <see cref="NoData"/>, range-folded gates <see cref="RangeFolded"/>.
/// </summary>
public sealed class SweepGeometry
{
    public required float[] EdgeAzimuthsRad;  // radialCount + 1, ascending

    /// <summary>
    /// The field the GPU filters: each gate's value multiplied by whether it has one, so an
    /// unmeasured gate contributes 0 to a weighted sum rather than a sentinel that would
    /// poison it. Paired with <see cref="Mask"/>, which carries the weights.
    /// </summary>
    public required float[] Values;           // radialCount * gateCount

    /// <summary>
    /// Two bytes a gate: 255 where the gate measured something, then 255 where it was range
    /// folded. Filtering these alongside <see cref="Values"/> is what lets a bilinear tap or
    /// a mip level come out as a mean over the gates that measured something, divided by how
    /// much of the footprint they covered — which the old single sentinel-encoded channel
    /// could not express, because there is no value that averages correctly with real data.
    /// </summary>
    public required byte[] Mask;              // radialCount * gateCount * 2
    public required int RadialCount;
    public required int GateCount;
    public required float FirstGateM;
    public required float GateSpacingM;
    public required float ElevationRad;
    public required double RadarLatDeg;
    public required double RadarLonDeg;

    public static SweepGeometry Build(Sweep sweep)
    {
        int n = sweep.RadialCount;
        int gates = sweep.GateCount;

        var order = Enumerable.Range(0, n)
            .OrderBy(i => sweep.AzimuthsDeg[i])
            .ToArray();

        // Wedge edges: midpoints between consecutive sorted azimuths, wrapping at north.
        var edges = new float[n + 1];
        for (int j = 1; j < n; j++)
        {
            float a = sweep.AzimuthsDeg[order[j - 1]];
            float b = sweep.AzimuthsDeg[order[j]];
            edges[j] = (a + b) * 0.5f * (MathF.PI / 180f);
        }
        float first = sweep.AzimuthsDeg[order[0]];
        float last = sweep.AzimuthsDeg[order[n - 1]];
        float wrapMid = ((first + 360f + last) * 0.5f) % 360f;
        // The wrap midpoint sits above the last azimuth; the first edge is the same
        // boundary expressed one turn lower.
        edges[n] = (last + (first + 360f - last) * 0.5f) * (MathF.PI / 180f);
        edges[0] = edges[n] - 2f * MathF.PI;
        _ = wrapMid;

        // Built here rather than at upload time because this runs on the thread pool and
        // the upload runs on the render thread, where the same loop over 1.3 million gates
        // measured 9 ms — a hitch on every new volume, and on every frame of an archive loop.
        var values = new float[n * gates];
        var mask = new byte[n * gates * 2];
        for (int j = 0; j < n; j++)
        {
            int src = order[j];
            var srcSpan = sweep.Data.AsSpan(src * gates, gates);
            int row = j * gates;
            for (int g = 0; g < gates; g++)
            {
                float v = srcSpan[g];
                bool valid = !float.IsNaN(v);
                values[row + g] = valid ? v : 0f;
                mask[(row + g) * 2] = valid ? (byte)255 : (byte)0;
                mask[(row + g) * 2 + 1] =
                    !valid && sweep.RangeFoldedMask[src * gates + g] ? (byte)255 : (byte)0;
            }
        }

        return new SweepGeometry
        {
            EdgeAzimuthsRad = edges,
            Values = values,
            Mask = mask,
            RadialCount = n,
            GateCount = gates,
            FirstGateM = sweep.FirstGateM,
            GateSpacingM = sweep.GateSpacingM,
            ElevationRad = sweep.ElevationAngleDeg * MathF.PI / 180f,
            RadarLatDeg = sweep.RadarLatDeg,
            RadarLonDeg = sweep.RadarLonDeg,
        };
    }
}
