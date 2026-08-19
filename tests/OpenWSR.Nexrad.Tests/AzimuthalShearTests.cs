using System.Collections;
using OpenWSR.Nexrad;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// No open-source reference implements LLSD azimuthal shear, so — following the project's
/// rule for that case — these assert physics rather than a reference's numbers. The
/// strongest are analytic: a field with a known constant gradient must return that
/// constant, and a vortex of known angular velocity must return it at the core.
/// </summary>
public class AzimuthalShearTests
{
    private const int Radials = 360;
    private const int Gates = 120;
    private const float FirstGateM = 2000;
    private const float GateSpacingM = 250;

    private static Sweep VelocitySweep(Func<int, int, float> value)
    {
        var data = new float[Radials * Gates];
        for (int r = 0; r < Radials; r++)
            for (int g = 0; g < Gates; g++)
                data[r * Gates + g] = value(r, g);

        return new Sweep(
            "KTLX", DateTime.UnixEpoch, 35.33, -97.28, 380,
            1, 0.5f, Moment.Velocity,
            [.. Enumerable.Range(0, Radials).Select(i => i * 360f / Radials)],
            FirstGateM, GateSpacingM, Gates, data,
            new ScaleInfo(2f, 129f, 8),
            new BitArray(Radials * Gates),
            26.12f);
    }

    private static double RangeM(int gate) => FirstGateM + gate * GateSpacingM;

    /// <summary>Mean of the finite values in a region, for comparing against an expectation.</summary>
    private static double MeanAt(Sweep sweep, int radial, int gate, int half = 3)
    {
        double sum = 0; int n = 0;
        for (int r = radial - half; r <= radial + half; r++)
            for (int g = gate - half; g <= gate + half; g++)
            {
                if (g < 0 || g >= sweep.GateCount) continue;
                int rr = ((r % Radials) + Radials) % Radials;
                float v = sweep.Data[rr * sweep.GateCount + g];
                if (!float.IsNaN(v)) { sum += v; n++; }
            }
        return n == 0 ? double.NaN : sum / n;
    }

    [Fact]
    public void UniformWindStaysBelowTheRotationThreshold()
    {
        // A uniform flow is what the product exists to remove: large velocity everywhere,
        // no rotation at all. It is not exactly zero — a uniform V reads as V/R, because
        // the same velocity difference is spread over a shorter arc closer in — so the bar
        // is that it stays under the 0.006 1/s where the palette starts calling something
        // rotation. That is what MinRangeM buys.
        var sweep = VelocitySweep((r, _) =>
            (float)(30.0 * Math.Cos(r * 2 * Math.PI / Radials)));
        var shear = AzimuthalShear.Compute(sweep);

        var finite = shear.Data.Where(v => !float.IsNaN(v)).ToArray();
        Assert.NotEmpty(finite);
        Assert.True(finite.Max(Math.Abs) < 0.0065,
            $"uniform wind produced {finite.Max(Math.Abs):F5} 1/s of shear");
    }

    [Fact]
    public void GatesInsideTheMinimumRangeReportNothing()
    {
        // Close in, ordinary wind reads as strong rotation. Reporting a number there would
        // paint the radar site the colour of a tornado on any windy day.
        var sweep = VelocitySweep((r, _) =>
            (float)(30.0 * Math.Cos(r * 2 * Math.PI / Radials)));
        var shear = AzimuthalShear.Compute(sweep);

        for (int g = 0; g < Gates; g++)
        {
            bool inside = RangeM(g) < AzimuthalShear.MinRangeM;
            bool reported = !float.IsNaN(shear.Data[90 * Gates + g]);
            Assert.True(inside != reported,
                $"gate {g} at {RangeM(g):F0} m: inside={inside} reported={reported}");
        }
    }

    [Fact]
    public void ConstantAzimuthalGradientIsRecoveredExactly()
    {
        // Build velocity as a known constant slope against azimuthal arc distance at every
        // range: v = k * (range * azimuth). The derivative must come back as k.
        const double k = 0.008; // 1/s
        var sweep = VelocitySweep((r, g) =>
        {
            double azimuthRad = r * 2 * Math.PI / Radials;
            // Keep away from the 0/360 seam so the ramp itself is continuous.
            double signed = azimuthRad > Math.PI ? azimuthRad - 2 * Math.PI : azimuthRad;
            return (float)(k * RangeM(g) * signed);
        });

        var shear = AzimuthalShear.Compute(sweep);

        // Sample well away from the seam, at a few ranges.
        foreach (int gate in new[] { 20, 60, 100 })
            Assert.Equal(k, MeanAt(shear, radial: 90, gate: gate), 4);
    }

    [Fact]
    public void CyclonicRotationReadsPositiveAndAnticyclonicNegative()
    {
        foreach (int sign in new[] { +1, -1 })
        {
            const double omega = 0.010; // 1/s of angular velocity
            var sweep = VortexSweep(omega * sign, centreRadial: 90, centreGate: 60);
            var shear = AzimuthalShear.Compute(sweep);

            double atCore = MeanAt(shear, radial: 90, gate: 60, half: 2);
            Assert.True(sign * atCore > 0,
                $"{(sign > 0 ? "cyclonic" : "anticyclonic")} vortex read {atCore:F5} 1/s");
        }
    }

    [Fact]
    public void SolidBodyVortexReturnsItsAngularVelocity()
    {
        // For a symmetric vortex sampled by one radar, azimuthal shear recovers the
        // angular velocity — half the vertical vorticity, which is the standard result.
        const double omega = 0.012;
        var sweep = VortexSweep(omega, centreRadial: 90, centreGate: 60);
        var shear = AzimuthalShear.Compute(sweep);

        double atCore = MeanAt(shear, radial: 90, gate: 60, half: 2);
        Assert.Equal(omega, atCore, 3);
    }

    [Fact]
    public void ShearFallsAwayFromTheVortex()
    {
        const double omega = 0.012;
        var sweep = VortexSweep(omega, centreRadial: 90, centreGate: 60);
        var shear = AzimuthalShear.Compute(sweep);

        double atCore = Math.Abs(MeanAt(shear, radial: 90, gate: 60, half: 2));
        // A quarter turn away, not the antipode: the fixture's own angular wrap sits at
        // 180 degrees from centre and would be measuring the test, not the code.
        double farAway = Math.Abs(MeanAt(shear, radial: 180, gate: 60, half: 3));
        Assert.True(atCore > 5 * farAway,
            $"core {atCore:F5} should stand well clear of the background {farAway:F5}");
    }

    [Fact]
    public void FoldedVelocityManufacturesShearThatUnfoldingRemoves()
    {
        // The reason this product depends on dealiasing. A fold is a 2*Nyquist step
        // between adjacent radials; differentiating across it invents shear far beyond
        // anything a real vortex produces.
        const float nyquist = 12f; // low, so the vortex folds
        const double omega = 0.010;
        var truth = VortexSweep(omega, centreRadial: 90, centreGate: 60, backgroundMs: 40);

        var foldedData = new float[truth.Data.Length];
        for (int i = 0; i < truth.Data.Length; i++)
        {
            float interval = 2 * nyquist;
            float f = (truth.Data[i] + nyquist) % interval;
            if (f < 0) f += interval;
            foldedData[i] = f - nyquist;
        }
        var folded = truth with { Data = foldedData, NyquistMs = nyquist };

        var fromFolded = AzimuthalShear.Compute(folded);
        var fromUnfolded = AzimuthalShear.Compute(VelocityDealiasing.Dealias(folded));

        double foldedPeak = fromFolded.Data.Where(v => !float.IsNaN(v)).Max(Math.Abs);
        double unfoldedPeak = fromUnfolded.Data.Where(v => !float.IsNaN(v)).Max(Math.Abs);

        // Folded, the artefact dwarfs the vortex it is supposed to reveal.
        Assert.True(foldedPeak > 4 * omega,
            $"expected a large aliasing artefact, saw {foldedPeak:F4} 1/s vs vortex {omega}");
        // Not to zero — the unfold is conservative and leaves a few folds standing — but
        // the artefact must stop dominating the field.
        Assert.True(unfoldedPeak < foldedPeak / 2,
            $"unfolding should remove it: {foldedPeak:F4} -> {unfoldedPeak:F4} 1/s");
    }

    [Fact]
    public void NoDataGatesProduceNoShear()
    {
        var sweep = VelocitySweep((_, _) => float.NaN);
        var shear = AzimuthalShear.Compute(sweep);
        Assert.All(shear.Data, v => Assert.True(float.IsNaN(v)));
    }

    [Fact]
    public void GeometryAndIdentityAreCarriedThrough()
    {
        var sweep = VelocitySweep((_, _) => 0f);
        var shear = AzimuthalShear.Compute(sweep);

        Assert.Equal(Moment.AzimuthalShear, shear.Moment);
        Assert.Equal(sweep.RadialCount, shear.RadialCount);
        Assert.Equal(sweep.GateCount, shear.GateCount);
        Assert.Equal(sweep.ElevationAngleDeg, shear.ElevationAngleDeg);
        Assert.Same(sweep.AzimuthsDeg, shear.AzimuthsDeg);
        Assert.NotSame(sweep.Data, shear.Data);
    }

    [Fact]
    public void ComputingFromANonVelocityMomentIsRejected()
    {
        var sweep = VelocitySweep((_, _) => 0f) with { Moment = Moment.Reflectivity };
        Assert.Throws<ArgumentException>(() => AzimuthalShear.Compute(sweep));
    }

    /// <summary>
    /// A solid-body vortex, projected onto the radar beam. Near the vortex the beams are
    /// close to parallel, so the measured velocity is the cross-beam offset times the
    /// angular velocity — positive angular velocity being counter-clockwise, i.e. cyclonic.
    /// </summary>
    private static Sweep VortexSweep(
        double omega, int centreRadial, int centreGate, double coreRadiusM = 4000,
        double backgroundMs = 0)
    {
        double centreAzimuth = centreRadial * 360.0 / Radials;
        double centreRange = RangeM(centreGate);

        return VelocitySweep((r, g) =>
        {
            double azimuth = r * 360.0 / Radials;
            double deltaDeg = ((azimuth - centreAzimuth + 540.0) % 360.0) - 180.0;
            double across = centreRange * deltaDeg * Math.PI / 180.0; // cross-beam offset
            double along = RangeM(g) - centreRange;                   // along-beam offset
            double distance = Math.Sqrt(across * across + along * along);

            // Solid body inside the core, falling off as 1/r outside it.
            double scale = distance <= coreRadiusM
                ? omega
                : omega * coreRadiusM * coreRadiusM / (distance * distance);
            return (float)(scale * across + backgroundMs);
        });
    }
}
