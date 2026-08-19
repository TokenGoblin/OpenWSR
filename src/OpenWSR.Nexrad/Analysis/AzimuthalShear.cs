namespace OpenWSR.Nexrad.Analysis;

/// <summary>
/// Azimuthal shear: how fast radial velocity changes as you move across the beam, in s⁻¹.
///
/// Rotation is what a velocity couplet means, but reading it off a velocity display is a
/// judgement call — the eye has to separate the couplet from the storm's own translation
/// and from the background flow. Differentiating across the beam removes both, because a
/// uniform wind has no azimuthal gradient. What is left is rotation, as a number.
///
/// The estimator is a linear least-squares derivative (Smith &amp; Elmore 2004), which is
/// what the operational rotation-track products use. Each fit is taken at constant range,
/// so the derivative is purely azimuthal, and several range gates are averaged to damp
/// the noise a three-radial fit would otherwise carry.
///
/// Sign is meteorological: <b>positive is cyclonic</b> (counter-clockwise in the northern
/// hemisphere). A cyclonic couplet puts inbound velocities at the lower azimuth and
/// outbound at the higher one, so velocity rises with azimuth.
///
/// <para>
/// Velocity must be unfolded first. A fold is a 2·V<sub>nyquist</sub> step between adjacent
/// radials, and differentiating across it manufactures shear far larger than any real
/// vortex — the aliasing artefact would drown the signal the product exists to show.
/// </para>
/// </summary>
public static class AzimuthalShear
{
    /// <summary>
    /// Radials either side of the centre in each fit. Five radials span about 4 km at
    /// 100 km range and under a kilometre close in — comfortably inside mesocyclone scale
    /// at the ranges where one is detectable at all.
    /// </summary>
    public const int RadialHalfWidth = 2;

    /// <summary>Range gates either side, averaged to damp noise. These do not enter the
    /// derivative; each fit is done at one range and the slopes are averaged.</summary>
    public const int GateHalfWidth = 1;

    /// <summary>
    /// A fit needs most of its samples. Below this the estimate is dominated by whichever
    /// few gates happened to have data, which at a storm edge is exactly where a spurious
    /// gradient would look most like rotation.
    /// </summary>
    private const int MinSamplesPerFit = 4;

    /// <summary>
    /// Gates closer than this report nothing.
    ///
    /// A real vortex's azimuthal shear is its angular velocity, and does not change with
    /// range. The *apparent* shear of ordinary wind does: a uniform flow of V metres per
    /// second reads as V/R, because the same velocity difference is spread over a shorter
    /// arc close in. For a 30 m/s wind that is
    ///
    ///   2 km -> 0.015 1/s     5 km -> 0.006 1/s     20 km -> 0.0015 1/s
    ///
    /// and 0.015 is mesocyclone territory. Without a floor, ordinary wind over the radar
    /// site paints the same colour as a tornado. Five kilometres puts the background at
    /// the very bottom of the scale while a genuine mesocyclone at 0.012 still stands
    /// clear of it — at the cost of not reporting a vortex directly overhead, which the
    /// velocity display shows plainly anyway.
    /// </summary>
    public const double MinRangeM = 5000;

    /// <summary>
    /// Compute azimuthal shear from a velocity sweep. The result carries the same geometry
    /// and <see cref="Moment.AzimuthalShear"/>; gates without a usable fit are NaN.
    /// </summary>
    public static Sweep Compute(Sweep velocity)
    {
        if (velocity.Moment != Moment.Velocity)
            throw new ArgumentException("Azimuthal shear is derived from velocity.", nameof(velocity));

        int radials = velocity.RadialCount, gates = velocity.GateCount;
        var shear = new float[radials * gates];
        Array.Fill(shear, float.NaN);

        // Each radial is independent, and a sweep is ~860k gates with a fifteen-sample fit
        // behind each one — enough that doing it serially is felt as a hitch.
        Parallel.For(0, radials, r =>
        {
            // Arc distance per radial step depends on range, so it is recomputed per gate.
            var offsets = new double[2 * RadialHalfWidth + 1];
            var values = new float[2 * RadialHalfWidth + 1];

            for (int g = 0; g < gates; g++)
            {
                if (float.IsNaN(velocity.Data[r * gates + g])) continue;
                if (velocity.FirstGateM + g * velocity.GateSpacingM < MinRangeM) continue;

                double sum = 0;
                int fits = 0;
                for (int dg = -GateHalfWidth; dg <= GateHalfWidth; dg++)
                {
                    int gate = g + dg;
                    if (gate < 0 || gate >= gates) continue;

                    double rangeM = velocity.FirstGateM + gate * velocity.GateSpacingM;
                    if (rangeM <= 0) continue;

                    if (TryFit(velocity, r, gate, rangeM, offsets, values, out double slope))
                    {
                        sum += slope;
                        fits++;
                    }
                }
                if (fits > 0) shear[r * gates + g] = (float)(sum / fits);
            }
        });

        return velocity with { Moment = Moment.AzimuthalShear, Data = shear };
    }

    /// <summary>
    /// Least-squares slope of velocity against azimuthal arc distance, at one range.
    /// Returns false when too few radials in the window carry data.
    /// </summary>
    private static bool TryFit(
        Sweep sweep, int centreRadial, int gate, double rangeM,
        double[] offsets, float[] values, out double slope)
    {
        slope = 0;
        int radials = sweep.RadialCount;
        int n = 0;
        double centreAzimuth = sweep.AzimuthsDeg[centreRadial];

        for (int dr = -RadialHalfWidth; dr <= RadialHalfWidth; dr++)
        {
            // Azimuth wraps, and so does the radial index.
            int radial = ((centreRadial + dr) % radials + radials) % radials;
            float value = sweep.Data[radial * sweep.GateCount + gate];
            if (float.IsNaN(value)) continue;

            // Signed angular difference in (-180, 180], so the fit is well behaved at north.
            double deltaDeg = ((sweep.AzimuthsDeg[radial] - centreAzimuth + 540.0) % 360.0) - 180.0;
            offsets[n] = rangeM * deltaDeg * Math.PI / 180.0; // arc length, metres
            values[n] = value;
            n++;
        }
        if (n < MinSamplesPerFit) return false;

        double meanOffset = 0, meanValue = 0;
        for (int i = 0; i < n; i++) { meanOffset += offsets[i]; meanValue += values[i]; }
        meanOffset /= n;
        meanValue /= n;

        double covariance = 0, variance = 0;
        for (int i = 0; i < n; i++)
        {
            double d = offsets[i] - meanOffset;
            covariance += d * (values[i] - meanValue);
            variance += d * d;
        }
        if (variance <= 0) return false; // all samples at the same azimuth

        slope = covariance / variance;
        return true;
    }
}
