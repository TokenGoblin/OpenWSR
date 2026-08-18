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
    public const float NoData = -1e30f;
    public const float RangeFolded = -2e30f;

    public required float[] EdgeAzimuthsRad;  // radialCount + 1, ascending
    public required float[] Data;             // radialCount * gateCount, sentinel-encoded
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

        var data = new float[n * gates];
        for (int j = 0; j < n; j++)
        {
            int src = order[j];
            var srcSpan = sweep.Data.AsSpan(src * gates, gates);
            var dstSpan = data.AsSpan(j * gates, gates);
            for (int g = 0; g < gates; g++)
            {
                float v = srcSpan[g];
                dstSpan[g] = float.IsNaN(v)
                    ? (sweep.RangeFoldedMask[src * gates + g] ? RangeFolded : NoData)
                    : v;
            }
        }

        return new SweepGeometry
        {
            EdgeAzimuthsRad = edges,
            Data = data,
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
