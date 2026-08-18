using OpenWSR.Geo;

namespace OpenWSR.Nexrad.Analysis;

/// <summary>A vertical slice through a volume along a ground path.</summary>
public sealed record CrossSectionResult(
    float[] Values,          // Width * Height, row 0 is the top; NaN where no beam samples
    int Width, int Height,
    double LengthKm,
    double MaxHeightKm,
    Moment Moment,
    IReadOnlyList<float> ElevationsUsed);

/// <summary>
/// Builds a vertical cross-section from a volume — the view GR2Analyst is bought for and
/// no open-source viewer offers. For every (distance, height) cell it works out which
/// elevation cut would have a beam passing through that point, then samples that sweep.
/// </summary>
public static class CrossSection
{
    /// <param name="sweeps">Every sweep of one moment from a volume.</param>
    public static CrossSectionResult Build(
        IReadOnlyList<Sweep> sweeps,
        double startLatDeg, double startLonDeg,
        double endLatDeg, double endLonDeg,
        int width = 640, int height = 220, double maxHeightKm = 15.0)
    {
        if (sweeps.Count == 0)
            throw new ArgumentException("A cross-section needs at least one sweep.", nameof(sweeps));

        var values = new float[width * height];
        Array.Fill(values, float.NaN);

        var ordered = sweeps.OrderBy(s => s.ElevationAngleDeg).ToList();
        var elevationsRad = ordered.Select(s => s.ElevationAngleDeg * Math.PI / 180.0).ToArray();

        double radarLat = ordered[0].RadarLatDeg;
        double radarLon = ordered[0].RadarLonDeg;
        double ka = GeoMath.EffectiveEarthRadiusM;

        double totalM = GeoMath.DistanceM(startLatDeg, startLonDeg, endLatDeg, endLonDeg);
        double pathBearing = GeoMath.BearingRad(startLatDeg, startLonDeg, endLatDeg, endLonDeg);

        for (int column = 0; column < width; column++)
        {
            double along = (column + 0.5) / width * totalM;
            var (lat, lon) = GeoMath.Offset(startLatDeg, startLonDeg, pathBearing, along);

            double groundM = GeoMath.DistanceM(radarLat, radarLon, lat, lon);
            double azimuthDeg = (GeoMath.BearingRad(radarLat, radarLon, lat, lon) * 180.0 / Math.PI + 360.0) % 360.0;
            double phi = groundM / ka; // earth-centre angle to this point

            for (int row = 0; row < height; row++)
            {
                double heightM = (1.0 - (row + 0.5) / height) * maxHeightKm * 1000.0;

                // Geometry from the radar to (ground arc, height) on the 4/3 earth.
                double horizontal = (ka + heightM) * Math.Sin(phi);
                double vertical = (ka + heightM) * Math.Cos(phi) - ka;
                double slantM = Math.Sqrt(horizontal * horizontal + vertical * vertical);
                if (slantM < 1) continue;
                double elevationRad = Math.Atan2(vertical, horizontal);

                float sample = SampleBetweenCuts(ordered, elevationsRad, elevationRad, azimuthDeg, slantM);
                if (!float.IsNaN(sample))
                    values[row * width + column] = sample;
            }
        }

        return new CrossSectionResult(
            values, width, height, totalM / 1000.0, maxHeightKm,
            ordered[0].Moment, [.. ordered.Select(s => s.ElevationAngleDeg)]);
    }

    /// <summary>Half a degree of slack, so the lowest and highest cuts keep some thickness.</summary>
    private const double EdgeToleranceRad = 0.5 * Math.PI / 180.0;

    /// <summary>
    /// Blend the two cuts that bracket the required elevation. A volume only samples
    /// along its beams, so without interpolation a slice is a handful of diagonal
    /// streaks rather than a picture of the storm. Outside the scanned cuts — below the
    /// lowest beam, or in the cone of silence overhead — nothing is returned.
    /// </summary>
    private static float SampleBetweenCuts(
        List<Sweep> sweeps, double[] elevations, double target, double azimuthDeg, double slantM)
    {
        if (target < elevations[0] - EdgeToleranceRad) return float.NaN;
        if (target > elevations[^1] + EdgeToleranceRad) return float.NaN;

        int upper = 0;
        while (upper < elevations.Length && elevations[upper] < target) upper++;

        if (upper == 0) return Sample(sweeps[0], azimuthDeg, slantM);
        if (upper >= elevations.Length) return Sample(sweeps[^1], azimuthDeg, slantM);

        int lower = upper - 1;
        float below = Sample(sweeps[lower], azimuthDeg, slantM);
        float above = Sample(sweeps[upper], azimuthDeg, slantM);
        if (float.IsNaN(below)) return above;
        if (float.IsNaN(above)) return below;

        double span = elevations[upper] - elevations[lower];
        double t = span > 1e-9 ? (target - elevations[lower]) / span : 0;
        return (float)(below + (above - below) * t);
    }

    private static float Sample(Sweep sweep, double azimuthDeg, double slantM)
    {
        int gate = (int)Math.Round((slantM - sweep.FirstGateM) / sweep.GateSpacingM);
        if (gate < 0 || gate >= sweep.GateCount) return float.NaN;

        int best = -1;
        double bestDelta = double.MaxValue;
        for (int i = 0; i < sweep.RadialCount; i++)
        {
            double delta = Math.Abs(((sweep.AzimuthsDeg[i] - azimuthDeg + 540.0) % 360.0) - 180.0);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = i;
            }
        }
        // Beyond about a degree we are no longer looking at the same beam.
        if (best < 0 || bestDelta > 1.5) return float.NaN;
        return sweep.Data[best * sweep.GateCount + gate];
    }
}
