using OpenWSR.Geo;

namespace OpenWSR.Nexrad.Analysis;

/// <summary>
/// A rotation-track swath: the strongest azimuthal shear seen at each point over a span of
/// volume scans, on a Mercator-aligned grid.
/// </summary>
public sealed record RotationTrackResult(
    float[] Values,       // Width * Height, row 0 is the top (maximum Mercator y); NaN = never sampled
    int Width, int Height,
    double MinX, double MinY, double MaxX, double MaxY,   // Mercator bounds, metres
    int ScanCount,
    DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>The strongest rotation anywhere in the swath, or NaN if nothing was sampled.</summary>
    public float Peak
    {
        get
        {
            float peak = float.NaN;
            foreach (var v in Values)
                if (!float.IsNaN(v) && (float.IsNaN(peak) || v > peak)) peak = v;
            return peak;
        }
    }
}

/// <summary>
/// Accumulates azimuthal shear across successive scans into a swath.
///
/// A single scan says where rotation is now. A damage survey, or the question "did that
/// couplet hold together for twenty minutes or fall apart", needs where rotation has
/// <em>been</em> — and a mesocyclone that tracks for fifty kilometres draws a line no
/// individual scan shows. Taking the maximum rather than the mean is what makes it a
/// track: a cell that rotated hard once keeps that value as the storm moves on.
///
/// Cyclonic only, by taking the signed maximum. Anticyclonic rotation is real but is a
/// different question, and mixing the two into an absolute value would let a strong
/// anticyclonic couplet masquerade as a tornado track.
/// </summary>
public static class RotationTracks
{
    /// <summary>
    /// Build a swath from per-scan shear sweeps, which must all come from the same site.
    /// Sweeps are expected to be <see cref="Moment.AzimuthalShear"/>; pass the same cut
    /// from each volume.
    /// </summary>
    public static RotationTrackResult Accumulate(
        IReadOnlyList<Sweep> shearSweeps, int resolution = 1024)
    {
        if (shearSweeps.Count == 0)
            throw new ArgumentException("A rotation track needs at least one scan.", nameof(shearSweeps));
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 64);

        var first = shearSweeps[0];
        double radarLat = first.RadarLatDeg, radarLon = first.RadarLonDeg;

        // Bounds: the widest scan's reach, boxed in Mercator. Mercator stretches with
        // latitude, so the box is taken from real offset points rather than by scaling.
        double maxRangeM = shearSweeps.Max(s => s.FirstGateM + (s.GateCount - 1) * s.GateSpacingM);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        for (int bearing = 0; bearing < 360; bearing += 15)
        {
            var (lat, lon) = GeoMath.Offset(radarLat, radarLon, bearing * Math.PI / 180.0, maxRangeM);
            var (x, y) = GeoMath.ToMercator(lat, lon);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        var values = new float[resolution * resolution];
        Array.Fill(values, float.NaN);
        double spanX = maxX - minX, spanY = maxY - minY;

        foreach (var raw in shearSweeps)
        {
            // Taking a maximum over a dozen scans is unforgiving of noise: one spurious
            // gate in one scan survives into the swath forever. Smoothing first is what
            // makes the accumulation about coherent rotation — a couplet spans many gates
            // and survives it, a lone hot gate does not.
            var sweep = Smooth(raw);
            var radialFor = AzimuthIndex.Build(sweep);
            double elevation = sweep.ElevationAngleDeg * Math.PI / 180.0;

            // Walking the output grid and projecting each cell back into polar space avoids
            // the holes a forward mapping leaves wherever gates spread wider than a cell.
            Parallel.For(0, resolution, row =>
            {
                double my = maxY - (row + 0.5) / resolution * spanY;
                for (int column = 0; column < resolution; column++)
                {
                    double mx = minX + (column + 0.5) / resolution * spanX;
                    var (lat, lon) = GeoMath.FromMercator(mx, my);

                    double groundRangeM = GeoMath.DistanceM(radarLat, radarLon, lat, lon);
                    if (groundRangeM > maxRangeM) continue;

                    // Gates are spaced along the slant path, not along the ground.
                    double delta = groundRangeM / GeoMath.EffectiveEarthRadiusM;
                    double slantM = GeoMath.EffectiveEarthRadiusM * Math.Sin(delta)
                                    / Math.Cos(elevation + delta);

                    int gate = (int)Math.Round((slantM - sweep.FirstGateM) / sweep.GateSpacingM);
                    if (gate < 0 || gate >= sweep.GateCount) continue;

                    double azimuthDeg = (GeoMath.BearingRad(radarLat, radarLon, lat, lon)
                                         * 180.0 / Math.PI + 360.0) % 360.0;
                    int radial = AzimuthIndex.RadialFor(radialFor, azimuthDeg);
                    if (radial < 0) continue;

                    float shear = sweep.Data[radial * sweep.GateCount + gate];
                    if (float.IsNaN(shear)) continue;

                    int index = row * resolution + column;
                    if (float.IsNaN(values[index]) || shear > values[index])
                        values[index] = shear;
                }
            });
        }

        return new RotationTrackResult(
            values, resolution, resolution, minX, minY, maxX, maxY,
            shearSweeps.Count,
            shearSweeps.Min(s => s.ScanTimeUtc),
            shearSweeps.Max(s => s.ScanTimeUtc));
    }

    /// <summary>
    /// Mean of each gate with its immediate neighbours, ignoring no-data. Wraps in azimuth
    /// for the same reason the region labelling does: without it the north seam becomes a
    /// permanent stripe in every swath.
    /// </summary>
    private static Sweep Smooth(Sweep sweep)
    {
        int radials = sweep.RadialCount, gates = sweep.GateCount;
        var result = new float[sweep.Data.Length];
        Array.Fill(result, float.NaN);

        Parallel.For(0, radials, r =>
        {
            for (int g = 0; g < gates; g++)
            {
                if (float.IsNaN(sweep.Data[r * gates + g])) continue;

                double sum = 0;
                int n = 0;
                for (int dr = -1; dr <= 1; dr++)
                {
                    int radial = ((r + dr) % radials + radials) % radials;
                    for (int dg = -1; dg <= 1; dg++)
                    {
                        int gate = g + dg;
                        if (gate < 0 || gate >= gates) continue;
                        float v = sweep.Data[radial * gates + gate];
                        if (float.IsNaN(v)) continue;
                        sum += v;
                        n++;
                    }
                }
                if (n > 0) result[r * gates + g] = (float)(sum / n);
            }
        });

        return sweep with { Data = result };
    }

    /// <summary>
    /// Nearest radial for each azimuth bin. Radials are not evenly spaced, and scanning
    /// them per sample would be a linear search inside a million-cell loop.
    /// </summary>
}
