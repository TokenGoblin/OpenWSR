namespace OpenWSR.Nexrad.Analysis;

/// <summary>
/// Blanks gates where a derived field is being computed from noise rather than from weather.
///
/// Azimuthal shear is a difference of velocities, and a velocity estimate needs something to
/// have reflected off. Where there is no scatterer the radar still reports *a* velocity —
/// the phase of receiver noise — so the shear derived from it is a random number, and a
/// product that takes a maximum over a dozen scans turns every one of those random numbers
/// into a permanent mark. Measured on the KTLX 2013-05-20 20:16Z volume, 88.8 % of gates
/// with strong cyclonic shear sat under reflectivity below 20 dBZ. That is the speckle.
/// </summary>
public static class GateQuality
{
    /// <summary>
    /// Below this there is not enough echo for a velocity to mean anything. On the Moore
    /// volume it removes 89 % of the strong-shear gates and leaves the peak <em>exactly</em>
    /// where it was, 0.1297 1/s over the tornado — which is the only test that matters,
    /// because a mask that also removes the signal has not helped:
    ///
    ///   threshold   strong shear gates   peak
    ///   none                      7397   0.1297 1/s
    ///   5 dBZ                     3174   0.1297
    ///   10 dBZ                    2074   0.1297
    ///   15 dBZ                    1343   0.1297
    ///   20 dBZ                     826   0.1297
    ///   25 dBZ                     561   0.1297
    /// </summary>
    public const float DefaultMinReflectivityDbz = 20f;

    /// <summary>
    /// Blank every gate of <paramref name="field"/> whose co-located reflectivity is missing
    /// or below <paramref name="minDbz"/>.
    ///
    /// The two sweeps are matched by azimuth and slant range rather than by index. On a
    /// split-cut VCP they usually do share a geometry — the Doppler cut carries reflectivity
    /// alongside velocity — but the surveillance cut at the same elevation has a different
    /// gate count, and a product that quietly indexed one with the other's numbers would be
    /// wrong by a few kilometres without ever looking wrong.
    /// </summary>
    public static Sweep MaskByReflectivity(
        Sweep field, Sweep reflectivity, float minDbz = DefaultMinReflectivityDbz)
    {
        var index = AzimuthIndex.Build(reflectivity);
        var masked = new float[field.Data.Length];

        Parallel.For(0, field.RadialCount, radial =>
        {
            int rowStart = radial * field.GateCount;
            int source = AzimuthIndex.RadialFor(index, field.AzimuthsDeg[radial]);
            if (source < 0)
            {
                // No matching beam at all: nothing vouches for these gates.
                for (int gate = 0; gate < field.GateCount; gate++)
                    masked[rowStart + gate] = float.NaN;
                return;
            }

            int sourceRow = source * reflectivity.GateCount;
            for (int gate = 0; gate < field.GateCount; gate++)
            {
                float value = field.Data[rowStart + gate];
                if (float.IsNaN(value))
                {
                    masked[rowStart + gate] = float.NaN;
                    continue;
                }

                double slantM = field.FirstGateM + gate * field.GateSpacingM;
                int sourceGate = (int)Math.Round(
                    (slantM - reflectivity.FirstGateM) / reflectivity.GateSpacingM);

                float dbz = sourceGate >= 0 && sourceGate < reflectivity.GateCount
                    ? reflectivity.Data[sourceRow + sourceGate]
                    : float.NaN;

                masked[rowStart + gate] = float.IsNaN(dbz) || dbz < minDbz ? float.NaN : value;
            }
        });

        return field with { Data = masked };
    }

    /// <summary>
    /// The reflectivity sweep that goes with a Doppler cut: the one from the same elevation
    /// index if the VCP recorded it there, and otherwise the nearest elevation angle that
    /// has reflectivity at all.
    ///
    /// On a split-cut VCP the Doppler cut carries its own reflectivity, so the first branch
    /// is the usual answer and the beams match exactly. The fallback matters for the older
    /// VCPs and for volumes that are still being scanned.
    /// </summary>
    public static Sweep? ReflectivityFor(IReadOnlyList<Sweep> volumeSweeps, Sweep dopplerCut)
    {
        var candidates = volumeSweeps.Where(s => s.Moment == Moment.Reflectivity).ToList();
        if (candidates.Count == 0) return null;

        return candidates.FirstOrDefault(s => s.ElevationIndex == dopplerCut.ElevationIndex)
               ?? candidates.MinBy(s => Math.Abs(s.ElevationAngleDeg - dopplerCut.ElevationAngleDeg));
    }
}
