using OpenWSR.Geo;

namespace OpenWSR.Nexrad.Analysis;

/// <summary>
/// A volume scan resampled onto a Cartesian box, ready to be a 3D texture.
///
/// One byte per voxel, and <b>zero means nothing was sampled here</b> rather than "very
/// weak". That distinction is the whole point: a radar volume is mostly empty space —
/// the cone of silence overhead, everything under the lowest beam, everything above the
/// highest cut — and a renderer has to be able to see straight through those places. A
/// scale that started at zero would paint the sky.
/// </summary>
public sealed record VolumeGrid(
    byte[] Voxels,
    int Nx, int Ny, int Nz,
    double HalfWidthM,
    double BaseHeightM,
    double TopHeightM,
    float EncodedMin,
    float EncodedMax,
    Moment Moment,
    double RadarLatDeg,
    double RadarLonDeg,
    DateTime ScanTimeUtc,
    IReadOnlyList<float> ElevationsUsed)
{
    /// <summary>x fastest, then y, then z — the layout a 3D texture upload expects.</summary>
    public int Index(int x, int y, int z) => x + Nx * (y + Ny * z);

    /// <summary>How many voxels hold a measurement. Mostly of interest as a sanity check.</summary>
    public int FilledCount
    {
        get
        {
            int n = 0;
            foreach (byte v in Voxels) if (v != 0) n++;
            return n;
        }
    }

    /// <summary>Decode a voxel back to the physical value, or NaN where nothing was sampled.</summary>
    public float Decode(byte voxel) => voxel == 0
        ? float.NaN
        : EncodedMin + (voxel - 1) / 254f * (EncodedMax - EncodedMin);
}

/// <summary>
/// Resamples a volume scan from its native polar frame onto a Cartesian grid.
///
/// The grid is walked and each cell asks which beam would have passed through it, rather
/// than the beams being walked and splattered into cells. A forward mapping leaves holes
/// wherever gates spread wider than a cell, and at 150 km a half-degree beam is over a
/// kilometre across, so the holes would be most of the picture.
/// </summary>
public static class VolumeGrid3D
{
    /// <summary>
    /// Half a degree of slack at the top and bottom cut, so the lowest and highest sweeps
    /// have some thickness instead of being infinitely thin surfaces. Matches
    /// <see cref="CrossSection"/>, and it matters that they match: a slice through this
    /// grid and a cross-section of the same volume should agree.
    /// </summary>
    private const double EdgeToleranceRad = 0.5 * Math.PI / 180.0;

    /// <summary>Beyond this from the nearest radial we are no longer on the same beam.</summary>
    private const double MaxAzimuthGapDeg = 1.5;

    /// <param name="sweeps">Every sweep of one moment from a volume.</param>
    /// <param name="encodeMin">Physical value that encodes to 1; anything below is clamped.</param>
    /// <param name="encodeMax">Physical value that encodes to 255.</param>
    public static VolumeGrid Build(
        IReadOnlyList<Sweep> sweeps,
        float encodeMin, float encodeMax,
        int horizontalCells = 256,
        int verticalCells = 48,
        double halfWidthM = 150_000,
        double topHeightM = 20_000,
        double baseHeightM = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(horizontalCells, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(verticalCells, 2);
        if (sweeps.Count == 0)
            throw new ArgumentException("A volume grid needs at least one sweep.", nameof(sweeps));
        if (encodeMax <= encodeMin)
            throw new ArgumentException("The encoding range must be non-empty.", nameof(encodeMax));

        var ordered = sweeps.OrderBy(s => s.ElevationAngleDeg).ToList();
        var elevationsRad = ordered.Select(s => s.ElevationAngleDeg * Math.PI / 180.0).ToArray();
        var azimuthIndex = ordered.Select(AzimuthIndex.Build).ToArray();

        // Past the farthest gate of the farthest-reaching cut there is nothing to find, and
        // the check is much cheaper than the sampling it skips.
        double maxSlantM = ordered.Max(s => s.FirstGateM + s.GateCount * s.GateSpacingM);

        int nx = horizontalCells, ny = horizontalCells, nz = verticalCells;
        var voxels = new byte[nx * ny * nz];

        double cell = 2.0 * halfWidthM / nx;
        double layer = (topHeightM - baseHeightM) / nz;
        float span = encodeMax - encodeMin;

        Parallel.For(0, nz, iz =>
        {
            double height = baseHeightM + (iz + 0.5) * layer;
            int planeBase = nx * ny * iz;

            for (int iy = 0; iy < ny; iy++)
            {
                // Northing, with row 0 at the south edge; the renderer's y axis is north.
                double north = -halfWidthM + (iy + 0.5) * cell;
                int rowBase = planeBase + nx * iy;

                for (int ix = 0; ix < nx; ix++)
                {
                    double east = -halfWidthM + (ix + 0.5) * cell;
                    double groundM = Math.Sqrt(east * east + north * north);
                    if (groundM > halfWidthM) continue;   // a disc, not a square: the radar
                                                          // has no more range in the corners

                    var (elevationRad, slantM) = GeoMath.BeamAngleTo(groundM, height);
                    if (slantM < 1 || slantM > maxSlantM) continue;

                    double azimuthDeg = Math.Atan2(east, north) * 180.0 / Math.PI;
                    if (azimuthDeg < 0) azimuthDeg += 360.0;

                    float value = SampleBetweenCuts(
                        ordered, elevationsRad, azimuthIndex, elevationRad, azimuthDeg, slantM);
                    if (float.IsNaN(value)) continue;

                    float t = (value - encodeMin) / span;
                    t = t < 0 ? 0 : t > 1 ? 1 : t;
                    voxels[rowBase + ix] = (byte)(1 + (int)(t * 254f + 0.5f));
                }
            }
        });

        return new VolumeGrid(
            voxels, nx, ny, nz, halfWidthM, baseHeightM, topHeightM,
            encodeMin, encodeMax, ordered[0].Moment,
            ordered[0].RadarLatDeg, ordered[0].RadarLonDeg, ordered[0].ScanTimeUtc,
            [.. ordered.Select(s => s.ElevationAngleDeg)]);
    }

    /// <summary>
    /// Blend the two cuts bracketing the required elevation, exactly as
    /// <see cref="CrossSection"/> does — including blending in the moment's own units
    /// rather than in linear power. That is the convention every radar display uses, and
    /// consistency between the 3D view and a cross-section of the same volume is worth
    /// more here than a decibel of theoretical correctness.
    ///
    /// Outside the scanned cuts nothing is returned: not below the lowest beam, and not in
    /// the cone of silence overhead. Those really are unmeasured, and inventing values for
    /// them would put a roof on every storm.
    /// </summary>
    private static float SampleBetweenCuts(
        List<Sweep> sweeps, double[] elevations, int[][] azimuthIndex,
        double target, double azimuthDeg, double slantM)
    {
        if (target < elevations[0] - EdgeToleranceRad) return float.NaN;
        if (target > elevations[^1] + EdgeToleranceRad) return float.NaN;

        int upper = 0;
        while (upper < elevations.Length && elevations[upper] < target) upper++;

        if (upper == 0) return Sample(sweeps[0], azimuthIndex[0], azimuthDeg, slantM);
        if (upper >= elevations.Length) return Sample(sweeps[^1], azimuthIndex[^1], azimuthDeg, slantM);

        int lower = upper - 1;
        float below = Sample(sweeps[lower], azimuthIndex[lower], azimuthDeg, slantM);
        float above = Sample(sweeps[upper], azimuthIndex[upper], azimuthDeg, slantM);
        if (float.IsNaN(below)) return above;
        if (float.IsNaN(above)) return below;

        double gap = elevations[upper] - elevations[lower];
        double t = gap > 1e-9 ? (target - elevations[lower]) / gap : 0;
        return (float)(below + (above - below) * t);
    }

    private static float Sample(Sweep sweep, int[] index, double azimuthDeg, double slantM)
    {
        int gate = (int)((slantM - sweep.FirstGateM) / sweep.GateSpacingM + 0.5);
        if (gate < 0 || gate >= sweep.GateCount) return float.NaN;

        int radial = AzimuthIndex.RadialFor(index, azimuthDeg);
        if (radial < 0) return float.NaN;

        double delta = Math.Abs(((sweep.AzimuthsDeg[radial] - azimuthDeg + 540.0) % 360.0) - 180.0);
        if (delta > MaxAzimuthGapDeg) return float.NaN;

        return sweep.Data[radial * sweep.GateCount + gate];
    }
}
