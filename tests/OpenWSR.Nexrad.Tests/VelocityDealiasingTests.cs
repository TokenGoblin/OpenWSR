using OpenWSR.Nexrad;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Dealiasing has no single "right answer" to compare against gate by gate, so these
/// assert the two things that are actually true of a correct unfold: it recovers a known
/// field that was folded synthetically, and it only ever moves a value by whole aliasing
/// intervals. The synthetic cases are the strong ones — the truth is known exactly.
/// </summary>
public class VelocityDealiasingTests
{
    private const int Radials = 360;
    private const int Gates = 200;

    /// <summary>Wrap a velocity into ±nyquist the way the radar's phase measurement does.</summary>
    private static float Fold(float trueMs, float nyquist)
    {
        float interval = 2 * nyquist;
        float folded = (trueMs + nyquist) % interval;
        if (folded < 0) folded += interval;
        return folded - nyquist;
    }

    /// <summary>
    /// Radial component of a uniform horizontal wind: the textbook velocity pattern, and
    /// the one whose folding is unmistakable — a wind faster than Nyquist puts a false
    /// inbound/outbound boundary right through the middle of the sweep.
    /// </summary>
    private static float[] UniformWind(float speedMs, float fromDirectionDeg)
    {
        var field = new float[Radials * Gates];
        double dir = fromDirectionDeg * Math.PI / 180.0;
        double u = -speedMs * Math.Sin(dir);
        double v = -speedMs * Math.Cos(dir);
        for (int r = 0; r < Radials; r++)
        {
            double az = r * 2 * Math.PI / Radials;
            float vr = (float)(u * Math.Sin(az) + v * Math.Cos(az));
            for (int g = 0; g < Gates; g++)
                field[r * Gates + g] = vr;
        }
        return field;
    }

    private static float[] Map(float[] source, Func<float, float> f)
    {
        var result = new float[source.Length];
        for (int i = 0; i < source.Length; i++) result[i] = f(source[i]);
        return result;
    }

    [Fact]
    public void FoldingHelperMatchesTheHardwareBehaviour()
    {
        // Sanity on the test's own fixture: 35 m/s at a 25 m/s Nyquist reads as -15.
        Assert.Equal(-15f, Fold(35f, 25f), 3);
        Assert.Equal(15f, Fold(-35f, 25f), 3);
        Assert.Equal(10f, Fold(10f, 25f), 3);
    }

    [Fact]
    public void RecoversAUniformWindFasterThanNyquist()
    {
        const float nyquist = 25f, interval = 2 * nyquist;
        var truth = UniformWind(speedMs: 40, fromDirectionDeg: 230);
        var folded = Map(truth, v => Fold(v, nyquist));

        // The fixture has to actually exercise the problem.
        Assert.Contains(folded.Zip(truth), p => Math.Abs(p.First - p.Second) > 1f);

        var dealiased = VelocityDealiasing.DealiasGrid(folded, Radials, Gates, interval);

        // Dealiasing recovers the field only up to one global interval — nothing in the
        // data says which absolute interval the whole sweep sits in. So the offset must be
        // a whole interval, and must be the *same* one everywhere: that is the shape.
        double offset = dealiased[0] - truth[0];
        Assert.Equal(0, Math.Round(offset / interval) * interval - offset, 2);
        for (int i = 0; i < truth.Length; i++)
            Assert.Equal(offset, dealiased[i] - truth[i], 2);
    }

    [Theory]
    [InlineData(30f, 20f)]
    [InlineData(45f, 25f)]
    [InlineData(60f, 22f)]   // nearly three intervals of fold
    [InlineData(19f, 20f)]   // no folding at all: must be left alone
    public void RecoversUniformWindsAcrossFoldDepths(float speed, float nyquist)
    {
        float interval = 2 * nyquist;
        var truth = UniformWind(speed, fromDirectionDeg: 115);
        var folded = Map(truth, v => Fold(v, nyquist));

        var dealiased = VelocityDealiasing.DealiasGrid(folded, Radials, Gates, interval);

        double offset = dealiased[0] - truth[0];
        for (int i = 0; i < truth.Length; i++)
            Assert.Equal(offset, dealiased[i] - truth[i], 2);
    }

    [Fact]
    public void RecoversTheSignOfAFoldedCouplet()
    {
        // A rotational couplet straddling the Nyquist limit: inbound on one side, outbound
        // on the other, both folded. This is the case that matters — folded, the couplet
        // reads with its sign reversed, which is the opposite of the truth.
        const float nyquist = 20f, interval = 2 * nyquist;
        var truth = new float[Radials * Gates];
        for (int r = 0; r < Radials; r++)
        {
            double az = r * 2 * Math.PI / Radials;
            for (int g = 0; g < Gates; g++)
            {
                // Smooth shear across the sweep, peaking at ±32 m/s.
                truth[r * Gates + g] = (float)(32.0 * Math.Sin(az));
            }
        }
        var folded = Map(truth, v => Fold(v, nyquist));

        // Folded, the peak outbound region reads inbound.
        int peakIndex = Radials / 4 * Gates;
        Assert.True(truth[peakIndex] > 30, "fixture should peak outbound");
        Assert.True(folded[peakIndex] < 0, "fixture should fold the peak to inbound");

        var dealiased = VelocityDealiasing.DealiasGrid(folded, Radials, Gates, interval);

        double offset = dealiased[0] - truth[0];
        Assert.Equal(offset, dealiased[peakIndex] - truth[peakIndex], 2);
        // With the shape recovered, the peak reads outbound again.
        Assert.True(dealiased[peakIndex] - offset > 30);
    }

    [Fact]
    public void OnlyEverShiftsByWholeIntervals()
    {
        const float nyquist = 24f, interval = 2 * nyquist;
        var truth = UniformWind(speedMs: 52, fromDirectionDeg: 300);
        var folded = Map(truth, v => Fold(v, nyquist));

        var dealiased = VelocityDealiasing.DealiasGrid(folded, Radials, Gates, interval);

        for (int i = 0; i < folded.Length; i++)
        {
            double shift = (dealiased[i] - folded[i]) / interval;
            Assert.Equal(Math.Round(shift), shift, 3);
        }
    }

    [Fact]
    public void NoDataGatesStayNoData()
    {
        const float nyquist = 25f, interval = 2 * nyquist;
        var folded = Map(UniformWind(40, 0), v => Fold(v, nyquist));
        for (int g = 0; g < Gates; g++) folded[10 * Gates + g] = float.NaN;

        var dealiased = VelocityDealiasing.DealiasGrid(folded, Radials, Gates, interval);

        for (int g = 0; g < Gates; g++)
            Assert.True(float.IsNaN(dealiased[10 * Gates + g]));
        Assert.DoesNotContain(dealiased.Take(Gates), float.IsNaN);
    }

    [Fact]
    public void AnAllNoDataSweepIsReturnedUnchanged()
    {
        var empty = new float[Radials * Gates];
        Array.Fill(empty, float.NaN);
        var dealiased = VelocityDealiasing.DealiasGrid(empty, Radials, Gates, 50f);
        Assert.All(dealiased, v => Assert.True(float.IsNaN(v)));
    }

    [Fact]
    public void SweepWithoutANyquistVelocityIsLeftAlone()
    {
        // Pre-2000 archives frequently carry no RRAD block; guessing an interval there
        // would invent velocities that were never measured.
        var sweep = SyntheticSweep(nyquist: null);
        Assert.Same(sweep, VelocityDealiasing.Dealias(sweep));
    }

    [Fact]
    public void NonVelocityMomentsAreLeftAlone()
    {
        var sweep = SyntheticSweep(nyquist: 25f) with { Moment = Moment.Reflectivity };
        Assert.Same(sweep, VelocityDealiasing.Dealias(sweep));
    }

    [Fact]
    public void DealiasedSweepKeepsItsGeometryAndMetadata()
    {
        var sweep = SyntheticSweep(nyquist: 25f);
        var result = VelocityDealiasing.Dealias(sweep);

        Assert.NotSame(sweep, result);
        Assert.NotSame(sweep.Data, result.Data);
        Assert.Equal(sweep.RadialCount, result.RadialCount);
        Assert.Equal(sweep.GateCount, result.GateCount);
        Assert.Same(sweep.AzimuthsDeg, result.AzimuthsDeg);
        Assert.Equal(sweep.ElevationAngleDeg, result.ElevationAngleDeg);
        Assert.Equal(sweep.NyquistMs, result.NyquistMs);
    }

    [Fact]
    public void AliasingIntervalIsTwiceNyquist()
    {
        Assert.Equal(50f, SyntheticSweep(nyquist: 25f).AliasingIntervalMs);
        Assert.Null(SyntheticSweep(nyquist: null).AliasingIntervalMs);
        Assert.Null(SyntheticSweep(nyquist: 0f).AliasingIntervalMs);
    }

    private static Sweep SyntheticSweep(float? nyquist)
    {
        var data = nyquist is { } n and > 0
            ? Map(UniformWind(40, 0), v => Fold(v, n))
            : new float[Radials * Gates];
        return new Sweep(
            "KTLX", DateTime.UnixEpoch, 35.33, -97.28, 380,
            1, 0.5f, Moment.Velocity,
            [.. Enumerable.Range(0, Radials).Select(i => i * 360f / Radials)],
            2125, 250, Gates, data,
            new ScaleInfo(2f, 129f, 8),
            new System.Collections.BitArray(Radials * Gates),
            nyquist);
    }

    [Fact]
    public void APatchReachableOnlyThroughNarrowBridgesIsStillCorrected()
    {
        // The case the old spanning-tree walk gave up on, and most of why it corrected only
        // two thirds of what Py-ART did. A folded core is walled off by no-data except for
        // three single-gate openings. Judged one at a time none of them looks like enough to
        // move a whole region; merged, they are one boundary three gate pairs wide, which is
        // how the reference implementation sees it.
        const float nyquist = 26.1f, interval = 2 * nyquist;
        const int radials = 40, gates = 60;

        // A real fold shows up as a jump of very nearly one interval, because the true
        // field is continuous across it — the surroundings sit just under the Nyquist
        // velocity and the core just over it.
        const float outside = 24f, coreTruth = 34f;
        float coreReported = Fold(coreTruth, nyquist);

        var field = new float[radials * gates];
        Array.Fill(field, outside);

        // Wall the core off completely...
        for (int r = 14; r < 27; r++)
            for (int g = 24; g < 47; g++)
                if (r is 14 or 26 || g is 24 or 46)
                    field[r * gates + g] = float.NaN;

        // ...then reopen exactly three single-gate bridges through the wall.
        foreach (int r in new[] { 17, 20, 23 }) field[r * gates + 24] = outside;

        for (int r = 15; r < 26; r++)
            for (int g = 25; g < 46; g++)
                field[r * gates + g] = coreReported;

        // The fixture has to pose the problem: the core must read as folded.
        Assert.True(coreReported < 0, "the core should be reported inbound after folding");
        Assert.True(Math.Abs(coreReported - coreTruth) > interval / 2);

        var dealiased = VelocityDealiasing.DealiasGrid(field, radials, gates, interval);

        // The core comes back as the outbound flow it is, and the surroundings are untouched.
        Assert.Equal(coreTruth, dealiased[20 * gates + 35], 2);
        Assert.Equal(coreTruth, dealiased[16 * gates + 30], 2);
        Assert.Equal(outside, dealiased[2 * gates + 5], 2);
    }

    [Fact]
    public void AnIsolatedPatchWithNoBridgeAtAllIsLeftAlone()
    {
        // The converse, and why the anchoring is on the largest region rather than on a zero
        // mean: with nothing joining it to the rest of the sweep there is no evidence about
        // which interval it belongs in, so the radar's own reading has to stand.
        const float nyquist = 26.1f, interval = 2 * nyquist;
        const int radials = 40, gates = 60;
        const float outside = 24f;
        float coreReported = Fold(34f, nyquist);

        var field = new float[radials * gates];
        Array.Fill(field, outside);

        for (int r = 14; r < 27; r++)
            for (int g = 24; g < 47; g++)
                if (r is 14 or 26 || g is 24 or 46)
                    field[r * gates + g] = float.NaN;

        for (int r = 15; r < 26; r++)
            for (int g = 25; g < 46; g++)
                field[r * gates + g] = coreReported;

        var dealiased = VelocityDealiasing.DealiasGrid(field, radials, gates, interval);

        Assert.Equal(coreReported, dealiased[20 * gates + 35], 2);
        Assert.Equal(outside, dealiased[2 * gates + 5], 2);
    }

}

/// <summary>
/// The same algorithm against the committed KTLX volume from 20 May 2013 20:16Z — the
/// Moore EF5, where velocities genuinely exceed the 26.12 m/s Nyquist. Reference values
/// come from Py-ART 2.2.5's independent <c>dealias_region_based</c>, dumped by
/// <c>resources/crosscheck/dealias_pyart.py</c>.
///
/// Gate-for-gate identity is not the bar and is not claimed: the two implementations
/// differ in how much evidence they demand before committing to a fold. What is asserted
/// is that both find the same Nyquist, over the same gates, and place the corrections in
/// the same parts of the sweep.
/// </summary>
public class VelocityDealiasingGoldenTests(DecodedVolumes volumes) : IClassFixture<DecodedVolumes>
{
    /// <summary>The Doppler cut at 0.53°; Py-ART calls the same data its sweep 1 at 0.48°.</summary>
    private Sweep LowestVelocityCut() =>
        volumes.Moore.Sweeps.First(s => s.Moment == Moment.Velocity);

    private Sweep VelocityCutAt(int elevationIndex) =>
        volumes.Moore.Sweeps.First(s => s.Moment == Moment.Velocity && s.ElevationIndex == elevationIndex);

    [Fact]
    public void NyquistVelocityMatchesPyArt()
    {
        // Py-ART: nyquist=26.1200 m/s on every velocity sweep of this volume.
        var sweep = LowestVelocityCut();
        Assert.NotNull(sweep.NyquistMs);
        Assert.Equal(26.12f, sweep.NyquistMs!.Value, 2);
        Assert.Equal(52.24f, sweep.AliasingIntervalMs!.Value, 2);
    }

    [Theory]
    [InlineData(2, 155912)]   // Py-ART sweep 1 (0.48 deg): valid=155912
    [InlineData(4, 156542)]   // Py-ART sweep 3 (0.88 deg): valid=156542
    [InlineData(6, 151743)]   // Py-ART sweep 5 (1.32 deg): valid=151743
    public void ValidGateCountMatchesPyArt(int elevationIndex, int expected)
    {
        var sweep = VelocityCutAt(elevationIndex);
        Assert.Equal(expected, sweep.Data.Count(v => !float.IsNaN(v)));
    }

    [Fact]
    public void RawVelocityIsPinnedAtTheFoldLimit()
    {
        // The signature of aliasing: the raw field cannot exceed the Nyquist, so a strong
        // flow saturates against it rather than reading its true value.
        var sweep = LowestVelocityCut();
        var valid = sweep.Data.Where(v => !float.IsNaN(v)).ToArray();
        Assert.Equal(-26.0f, valid.Min(), 1);   // Py-ART: raw min=-26.00
        Assert.Equal(26.0f, valid.Max(), 1);    // Py-ART: raw max=26.00
    }

    [Fact]
    public void DealiasingOnlyShiftsByWholeIntervalsAndNeverMoreThanOne()
    {
        // A single boundary can never justify more than one interval, because the raw
        // field already spans exactly one. Anything beyond that came from a chain of
        // decisions walking out through noise, which is the failure this guards.
        var sweep = LowestVelocityCut();
        float interval = sweep.AliasingIntervalMs!.Value;
        var result = VelocityDealiasing.Dealias(sweep);

        for (int i = 0; i < sweep.Data.Length; i++)
        {
            if (float.IsNaN(sweep.Data[i])) continue;
            double shift = (result.Data[i] - sweep.Data[i]) / interval;
            Assert.Equal(Math.Round(shift), shift, 3);
            Assert.True(Math.Abs(shift) <= 1, $"gate {i} shifted {shift} intervals");
        }
    }

    [Fact]
    public void DealiasedVelocitiesStayWithinTheSingleUnfoldCeiling()
    {
        var sweep = LowestVelocityCut();
        float ceiling = sweep.NyquistMs!.Value + sweep.AliasingIntervalMs!.Value; // 78.36
        var result = VelocityDealiasing.Dealias(sweep);

        var valid = result.Data.Where(v => !float.IsNaN(v)).ToArray();
        Assert.True(valid.Max(Math.Abs) <= ceiling,
            $"peak {valid.Max(Math.Abs):F1} exceeds the {ceiling:F1} m/s ceiling");
        // And it does reach past the raw Nyquist, or nothing was unfolded at all.
        Assert.True(valid.Max(Math.Abs) > sweep.NyquistMs!.Value + 1);
    }

    [Theory]
    [InlineData(2, 0.0089)]   // Py-ART sweep 1: corrected=1393 (0.89%)
    [InlineData(6, 0.0271)]   // Py-ART sweep 5: corrected=4116 (2.71%)
    public void CorrectionRateIsTheSameOrderAsPyArt(int elevationIndex, double pyArtFraction)
    {
        var sweep = VelocityCutAt(elevationIndex);
        var result = VelocityDealiasing.Dealias(sweep);

        int valid = 0, corrected = 0;
        for (int i = 0; i < sweep.Data.Length; i++)
        {
            if (float.IsNaN(sweep.Data[i])) continue;
            valid++;
            if (Math.Abs(result.Data[i] - sweep.Data[i]) > 0.01f) corrected++;
        }
        double fraction = (double)corrected / valid;

        // Since the solver merges regions the way Py-ART does — combining the boundaries
        // of everything already merged, rather than judging each one alone — it reaches
        // 85-92 % of the reference's rate. The floor is set just under that so a
        // regression back to the old spanning-tree walk (53-68 %) fails here rather than
        // being noticed months later; the ceiling catches over-correction, which would
        // mean folds are being invented.
        Assert.InRange(fraction, pyArtFraction * 0.80, pyArtFraction * 1.1);
    }

    [Fact]
    public void CorrectionsClusterInTheSameSectorsPyArtFinds()
    {
        // Py-ART sweep 5, corrections by azimuth sector, in rank order:
        //   30-45 (1985), 210-225 (864), 195-210 (647), 15-30 (481)
        // A correct unfold is spatially coherent. Scattered single gates would mean noise
        // was being amplified instead.
        var sweep = VelocityCutAt(6);
        var result = VelocityDealiasing.Dealias(sweep);

        var bySector = new Dictionary<int, int>();
        for (int i = 0; i < sweep.Data.Length; i++)
        {
            if (float.IsNaN(sweep.Data[i])) continue;
            if (Math.Abs(result.Data[i] - sweep.Data[i]) < 0.01f) continue;
            int sector = (int)(sweep.AzimuthsDeg[i / sweep.GateCount] / 15) * 15;
            bySector[sector] = bySector.GetValueOrDefault(sector) + 1;
        }

        var top = bySector.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key).ToHashSet();
        Assert.Contains(30, top);    // the dominant outbound sector in both
        Assert.Contains(210, top);   // and the inbound one opposite it
        Assert.True(top.IsSubsetOf(new HashSet<int> { 15, 30, 195, 210, 255 }),
            $"corrections landed in unexpected sectors: {string.Join(",", top.Order())}");
    }
}
