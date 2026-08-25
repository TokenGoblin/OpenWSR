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
    /// A low-CC gate is only condemned as clutter when its echo is below this. Debris sits
    /// above it; clutter and biologicals sit below. See <see cref="MaskClutter"/>.
    /// </summary>
    public const float ClutterMaxReflectivityDbz = 40f;

    /// <summary>Below this, a gate is not a meteorological target.</summary>
    public const float ClutterMaxCorrelation = 0.85f;

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
        var dbz = Resample(reflectivity, field);
        var masked = new float[field.Data.Length];

        Parallel.For(0, field.Data.Length, i =>
            masked[i] = float.IsNaN(dbz[i]) || dbz[i] < minDbz ? float.NaN : field.Data[i]);

        return field with { Data = masked };
    }

    /// <summary>
    /// A gate whose echo is weak <em>and</em> whose correlation coefficient is low is not
    /// weather. Blank it.
    /// </summary>
    /// <remarks>
    /// Ground and sea clutter return strongly enough to pass the reflectivity mask above, so
    /// they survive it as radial spikes. Correlation coefficient is the field that identifies
    /// them — but on its own it is the worst possible thing to threshold here, because a
    /// tornadic debris signature <em>is</em> a low-CC target. Measured on the Moore volume, a
    /// bare CC &lt; 0.85 mask removes all 89 debris gates and moves the shear peak off the
    /// tornado entirely, from 0.1297 1/s at 35.323,-97.527 to 0.1000 at 35.276,-97.282. It
    /// finds the wrong storm feature.
    ///
    /// What separates them is that debris is a <em>strong</em> echo with low CC — that is what
    /// makes a TDS detectable — while clutter and biological scatterers are low-CC and weak.
    /// So low CC only condemns a gate when the echo is also weak, and 40 dBZ is where the
    /// curve turns: the speckle removed has already saturated there while the cost is still
    /// exactly zero.
    ///
    ///   dBZ ceiling   debris kept   peak       speckle removed (KAMX/KBOX/KBYX/KTBW)
    ///   30                     89   0.1297     60 / 78 / 67 / 60 %
    ///   40                     89   0.1297     63 / 87 / 67 / 61 %
    ///   45                     72   0.1297     63 / 87 / 67 / 61 %
    ///   50                     55   0.1297     63 / 87 / 67 / 61 %
    ///   no ceiling              0   0.1000     63 / 87 / 67 / 61 %
    ///
    /// Spectrum width was the other candidate and is much weaker: debris is spectrally wide
    /// (median 5.5 m/s against 1.0 for clutter), but thresholding on it removes only 6-24 % of
    /// the speckle and costs debris gates doing it. Radial velocity does not separate them at
    /// all — the RDA's own clutter filter has already notched out the truly stationary
    /// returns, so what survives is not sitting at zero Doppler.
    /// </remarks>
    public static Sweep MaskClutter(
        Sweep field, Sweep reflectivity, Sweep correlation,
        float maxDbz = ClutterMaxReflectivityDbz, float maxCc = ClutterMaxCorrelation)
    {
        var dbz = Resample(reflectivity, field);
        var rho = Resample(correlation, field);
        var masked = new float[field.Data.Length];

        // A gate with nothing to judge it by is left alone: absent CC is not evidence.
        Parallel.For(0, field.Data.Length, i =>
            masked[i] = !float.IsNaN(rho[i]) && rho[i] < maxCc
                     && !float.IsNaN(dbz[i]) && dbz[i] < maxDbz
                ? float.NaN
                : field.Data[i]);

        return field with { Data = masked };
    }

    /// <summary>
    /// <paramref name="source"/> sampled onto <paramref name="field"/>'s geometry, NaN where
    /// there is no matching beam or gate.
    /// </summary>
    /// <remarks>
    /// Matched by azimuth and slant range rather than by index. On a split-cut VCP the two
    /// usually do share a geometry — the Doppler cut carries reflectivity alongside velocity —
    /// but the surveillance cut that carries CC has a different gate count, and a product that
    /// quietly indexed one with the other's numbers would be wrong by a few kilometres without
    /// ever looking wrong.
    /// </remarks>
    private static float[] Resample(Sweep source, Sweep field)
    {
        var index = AzimuthIndex.Build(source);
        var values = new float[field.Data.Length];

        Parallel.For(0, field.RadialCount, radial =>
        {
            int rowStart = radial * field.GateCount;
            int match = AzimuthIndex.RadialFor(index, field.AzimuthsDeg[radial]);
            if (match < 0)
            {
                // No matching beam at all: nothing vouches for these gates.
                for (int gate = 0; gate < field.GateCount; gate++)
                    values[rowStart + gate] = float.NaN;
                return;
            }

            int sourceRow = match * source.GateCount;
            for (int gate = 0; gate < field.GateCount; gate++)
            {
                double slantM = field.FirstGateM + gate * field.GateSpacingM;
                int sourceGate = (int)Math.Round(
                    (slantM - source.FirstGateM) / source.GateSpacingM);

                values[rowStart + gate] = sourceGate >= 0 && sourceGate < source.GateCount
                    ? source.Data[sourceRow + sourceGate]
                    : float.NaN;
            }
        });

        return values;
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

    /// <summary>
    /// The correlation-coefficient sweep that goes with a Doppler cut, or null when the VCP
    /// has none near it.
    /// </summary>
    /// <remarks>
    /// On a split-cut VCP, CC lives on the surveillance half — a different elevation index at
    /// a slightly different angle (0.60 deg against the Doppler cut's 0.53 deg on VCP 12), so
    /// it has to be matched by angle. The half-degree guard is what keeps a volume with no
    /// low-level dual-pol from silently borrowing a cut several degrees up and masking against
    /// the wrong altitude; returning null there leaves the field unmasked, which is the safe
    /// way to be wrong.
    /// </remarks>
    public static Sweep? CorrelationFor(IReadOnlyList<Sweep> volumeSweeps, Sweep dopplerCut)
    {
        var candidates = volumeSweeps
            .Where(s => s.Moment == Moment.CorrelationCoefficient)
            .ToList();
        if (candidates.Count == 0) return null;

        var nearest = candidates.MinBy(
            s => Math.Abs(s.ElevationAngleDeg - dopplerCut.ElevationAngleDeg))!;
        return Math.Abs(nearest.ElevationAngleDeg - dopplerCut.ElevationAngleDeg) <= 0.5
            ? nearest
            : null;
    }
}
