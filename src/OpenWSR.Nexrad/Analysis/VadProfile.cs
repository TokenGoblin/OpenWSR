using OpenWSR.Geo;

namespace OpenWSR.Nexrad.Analysis;

/// <summary>One height in the wind profile, with the fit quality that produced it.</summary>
public sealed record VadLevel(
    double AltitudeM,
    double SpeedMs,
    double DirectionDeg,   // meteorological: the direction the wind comes *from*
    double RmsMs,          // residual of the fit; how nearly the ring behaved like one wind
    int Samples,
    double ElevationDeg)   // which cut supplied it
{
    public double SpeedKnots => SpeedMs * 1.943844;
}

/// <summary>
/// The VAD wind profile: horizontal wind against height, over the radar.
///
/// A single radar measures only the component of motion along its own beam, but it
/// measures it all the way around. Ride a circle of constant range and the radial velocity
/// traces a sine wave whose amplitude is the wind speed and whose phase is its direction —
/// which is the whole Velocity Azimuth Display idea, and it turns one number per gate into
/// a wind.
///
/// <para>
/// This is computed from Level II rather than decoded from the Level III NVW product, on
/// purpose. NVW is still distributed today, but this project already carries a scar from
/// depending on a Level III product that stopped being generated — the storm-structure
/// product went away around 2021 and took the cell attributes with it. Deriving the profile
/// keeps it working on any volume back to 1991, at every scan rather than every tenth
/// minute, and makes it impossible to take away.
/// </para>
/// </summary>
public static class VadProfile
{
    /// <summary>
    /// Azimuth sectors, of twelve, that must contain data before a ring is trusted. A
    /// partial ring can be fitted, but a sine fitted through one quadrant is really just
    /// that quadrant's radial velocities wearing a wind's clothes.
    /// </summary>
    private const int RequiredSectors = 8;

    /// <summary>A ring whose residual is this large relative to its amplitude is not one wind.</summary>
    private const double MaxRmsFraction = 0.4;

    private const int SectorCount = 12;

    /// <summary>
    /// Fit the wind at each height. Velocity is unfolded first: a fold is a discontinuity
    /// around the ring, and least squares will happily average it into a wind that was
    /// never blowing.
    /// </summary>
    public static IReadOnlyList<VadLevel> Compute(
        IReadOnlyList<Sweep> velocitySweeps,
        double maxAltitudeM = 12_000,
        double layerM = 300)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layerM);

        var sweeps = velocitySweeps
            .Where(s => s.Moment == Moment.Velocity)
            .Select(VelocityDealiasing.Dealias)
            .OrderBy(s => s.ElevationAngleDeg)
            .ToList();
        if (sweeps.Count == 0) return [];

        var levels = new List<VadLevel>();
        for (double altitude = layerM; altitude <= maxAltitudeM; altitude += layerM)
        {
            VadLevel? best = null;
            foreach (var sweep in sweeps)
            {
                var level = FitRing(sweep, altitude);
                if (level is null) continue;
                // Prefer the cleanest fit; ties go to the lower cut, which is sampling a
                // tighter ring and therefore a more local wind.
                if (best is null || level.RmsMs < best.RmsMs) best = level;
            }
            if (best is not null) levels.Add(best);
        }
        return levels;
    }

    /// <summary>
    /// Fit one ring of constant height on one cut. Returns null when the ring is not on
    /// this cut, is too sparse, or does not behave like a single wind.
    /// </summary>
    private static VadLevel? FitRing(Sweep sweep, double altitudeM)
    {
        double elevation = sweep.ElevationAngleDeg * Math.PI / 180.0;
        double cosElevation = Math.Cos(elevation);
        if (cosElevation <= 0.05) return null; // looking almost straight up: no horizontal signal

        double slant = GeoMath.SlantRangeForHeight(altitudeM, elevation);
        int gate = (int)Math.Round((slant - sweep.FirstGateM) / sweep.GateSpacingM);
        if (gate < 1 || gate >= sweep.GateCount) return null;

        // Normal equations for v = a0 + a1 sin(az) + a2 cos(az).
        double n = 0, s = 0, c = 0, ss = 0, cc = 0, sc = 0;
        double vs = 0, vc = 0, v1 = 0;
        var sectors = new bool[SectorCount];

        for (int radial = 0; radial < sweep.RadialCount; radial++)
        {
            float value = sweep.Data[radial * sweep.GateCount + gate];
            if (float.IsNaN(value)) continue;

            double azimuth = sweep.AzimuthsDeg[radial] * Math.PI / 180.0;
            double sin = Math.Sin(azimuth), cos = Math.Cos(azimuth);

            n++; s += sin; c += cos;
            ss += sin * sin; cc += cos * cos; sc += sin * cos;
            v1 += value; vs += value * sin; vc += value * cos;
            sectors[(int)(sweep.AzimuthsDeg[radial] / (360.0 / SectorCount)) % SectorCount] = true;
        }
        if (n < 20 || sectors.Count(x => x) < RequiredSectors) return null;

        // Solve the 3x3 symmetric system by Cramer's rule; it is small and well behaved
        // once the ring has real azimuthal coverage.
        double[,] m =
        {
            { n, s, c },
            { s, ss, sc },
            { c, sc, cc },
        };
        double[] rhs = { v1, vs, vc };
        if (!Solve3(m, rhs, out double a0, out double a1, out double a2)) return null;

        // The fit is in beam-parallel units; the horizontal wind is larger by 1/cos(elev).
        double u = a1 / cosElevation;
        double v = a2 / cosElevation;
        double speed = Math.Sqrt(u * u + v * v);
        if (speed < 0.5) return null;

        // Residual, to say how nearly this ring behaved like one wind.
        double sumSquares = 0;
        int used = 0;
        for (int radial = 0; radial < sweep.RadialCount; radial++)
        {
            float value = sweep.Data[radial * sweep.GateCount + gate];
            if (float.IsNaN(value)) continue;
            double azimuth = sweep.AzimuthsDeg[radial] * Math.PI / 180.0;
            double predicted = a0 + a1 * Math.Sin(azimuth) + a2 * Math.Cos(azimuth);
            sumSquares += (value - predicted) * (value - predicted);
            used++;
        }
        double rms = Math.Sqrt(sumSquares / used);
        if (rms > MaxRmsFraction * speed * cosElevation) return null;

        // Meteorological convention: the direction the wind blows *from*.
        double direction = (Math.Atan2(-u, -v) * 180.0 / Math.PI + 360.0) % 360.0;
        return new VadLevel(altitudeM, speed, direction, rms, used, sweep.ElevationAngleDeg);
    }

    private static bool Solve3(double[,] m, double[] r, out double x0, out double x1, out double x2)
    {
        x0 = x1 = x2 = 0;
        double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
            a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

        double det = Det(m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2], m[2, 0], m[2, 1], m[2, 2]);
        if (Math.Abs(det) < 1e-9) return false;

        x0 = Det(r[0], m[0, 1], m[0, 2], r[1], m[1, 1], m[1, 2], r[2], m[2, 1], m[2, 2]) / det;
        x1 = Det(m[0, 0], r[0], m[0, 2], m[1, 0], r[1], m[1, 2], m[2, 0], r[2], m[2, 2]) / det;
        x2 = Det(m[0, 0], m[0, 1], r[0], m[1, 0], m[1, 1], r[1], m[2, 0], m[2, 1], r[2]) / det;
        return true;
    }
}
