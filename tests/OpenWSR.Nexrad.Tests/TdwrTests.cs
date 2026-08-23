using OpenWSR.Nexrad;
using OpenWSR.Nexrad.Level3;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Terminal Doppler Weather Radar through Level III, against committed products from TSLC —
/// the TDWR at Salt Lake City airport.
///
/// Reference values come from <b>MetPy 1.7.1</b>, which is this project's standard for Level
/// III. MetPy's <c>DigitalMapper</c> is the independent implementation of the same scaling.
/// </summary>
public class TdwrTests
{
    private static RadialImageProduct Reflectivity() => RadialImage.Decode(
        File.ReadAllBytes(TestData.Path("tdwr/SLC_TZ0_2026_08_23_05_36_00")));

    private static RadialImageProduct Velocity() => RadialImage.Decode(
        File.ReadAllBytes(TestData.Path("tdwr/SLC_TV0_2026_08_23_05_41_02")));

    private static readonly Lazy<RadialImageProduct> Z = new(Reflectivity);
    private static readonly Lazy<RadialImageProduct> V = new(Velocity);

    /// <summary>
    /// The existing packet-16 decoder reads these unchanged — a TDWR digital radial product is
    /// the same container as the WSR-88D ones, which is what makes this reachable at all.
    /// MetPy: code 180, site SLC, 40.967/-111.930, elevation 0.5°, 360 × 592.
    /// </summary>
    [Fact]
    public void ReadsTheProductHeader()
    {
        var z = Z.Value;

        Assert.Equal(180, z.ProductCode);
        Assert.Equal("SLC", z.SiteId);
        Assert.Equal(40.967, z.RadarLatDeg, 3);
        Assert.Equal(-111.930, z.RadarLonDeg, 3);
        Assert.Equal(0.5f, z.ElevationAngleDeg, 2);
        Assert.Equal(360, z.RadialCount);
        Assert.Equal(592, z.GateCount);
        Assert.Equal(new DateTime(2026, 8, 23, 5, 36, 0, DateTimeKind.Utc), z.VolumeTimeUtc);
    }

    [Fact]
    public void ReadsTheVelocityProductHeader()
    {
        var v = V.Value;

        Assert.Equal(182, v.ProductCode);
        Assert.Equal(0.5f, v.ElevationAngleDeg, 2);
        Assert.Equal(360, v.RadialCount);
        Assert.Equal(592, v.GateCount);
    }

    /// <summary>
    /// The scaling comes from the product's own halfwords rather than a table keyed on product
    /// code: 31 is the first data level in tenths, 32 the increment, 33 the level count.
    /// MetPy reads (-320, 5, 254) for reflectivity and (-635, 5, 254) for velocity — the same
    /// half-unit step from two different floors, which is exactly why it cannot be hardcoded.
    /// </summary>
    [Fact]
    public void TheScalingIsReadFromTheProduct()
    {
        Assert.Equal(-320, (short)Z.Value.Thresholds[0]);
        Assert.Equal(5, Z.Value.Thresholds[1]);
        Assert.Equal(254, Z.Value.Thresholds[2]);

        Assert.Equal(-635, (short)V.Value.Thresholds[0]);
        Assert.Equal(5, V.Value.Thresholds[1]);
    }

    /// <summary>Individual gates, mapped by MetPy from the same bytes.</summary>
    [Theory]
    [InlineData(0, 50, -6.0)]
    [InlineData(270, 50, 31.0)]
    public void ReflectivityGatesMatchMetpy(int radial, int gate, double dbz) =>
        Assert.Equal(dbz, TdwrRadar.Value(
            Z.Value.LevelAt(radial, gate), Z.Value.Thresholds), 3);

    [Theory]
    [InlineData(0, 50, 3.0)]
    public void VelocityGatesMatchMetpy(int radial, int gate, double metresPerSecond) =>
        Assert.Equal(metresPerSecond, TdwrRadar.Value(
            V.Value.LevelAt(radial, gate), V.Value.Thresholds), 3);

    /// <summary>
    /// The whole field, which is the check a single gate cannot make: MetPy maps 82,596 of the
    /// 213,120 reflectivity gates to a value, spanning -22.0 to 57.0 dBZ.
    /// </summary>
    [Fact]
    public void TheWholeReflectivityFieldMatchesMetpy()
    {
        var z = Z.Value;
        var values = z.Levels.Select(l => TdwrRadar.Value(l, z.Thresholds))
            .Where(v => !float.IsNaN(v)).ToArray();

        Assert.Equal(82_596, values.Length);
        Assert.Equal(-22.0, values.Min(), 3);
        Assert.Equal(57.0, values.Max(), 3);
    }

    /// <summary>MetPy: 62,311 velocity gates, -27.5 to +38.5 m/s.</summary>
    [Fact]
    public void TheWholeVelocityFieldMatchesMetpy()
    {
        var v = V.Value;
        var values = v.Levels.Select(l => TdwrRadar.Value(l, v.Thresholds))
            .Where(x => !float.IsNaN(x)).ToArray();

        Assert.Equal(62_311, values.Length);
        Assert.Equal(-27.5, values.Min(), 3);
        Assert.Equal(38.5, values.Max(), 3);
    }

    /// <summary>
    /// Levels 0 and 1 are flags, not data. Scaling them as though they were would put
    /// "below threshold" at -33 dBZ and range-folded gates at -32.5 — plausible numbers in a
    /// field whose floor is -32, which is what makes this worth asserting rather than assuming.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheTwoFlagLevelsAreNotValues(byte level) =>
        Assert.True(float.IsNaN(TdwrRadar.Value(level, Z.Value.Thresholds)));

    /// <summary>The first data level is the floor the header states, exactly.</summary>
    [Fact]
    public void LevelTwoIsTheStatedMinimum()
    {
        Assert.Equal(-32.0, TdwrRadar.Value(2, Z.Value.Thresholds), 3);
        Assert.Equal(-63.5, TdwrRadar.Value(2, V.Value.Thresholds), 3);
    }

    // ---- becoming a sweep ----

    /// <summary>
    /// Converted to a <see cref="Sweep"/> the whole existing stack applies — renderer,
    /// palettes, inspector, cross-section — because they all speak sweeps. The conversion has
    /// to preserve the values, not just the shape.
    /// </summary>
    [Fact]
    public void TheSweepCarriesTheSameValues()
    {
        var z = Z.Value;
        var sweep = TdwrRadar.ToSweep(z, Moment.Reflectivity, altitudeM: 1288);

        Assert.Equal("SLC", sweep.SiteId);
        Assert.Equal(360, sweep.RadialCount);
        Assert.Equal(592, sweep.GateCount);
        Assert.Equal(Moment.Reflectivity, sweep.Moment);
        Assert.Equal(0.5f, sweep.ElevationAngleDeg, 2);
        Assert.Equal(1288, sweep.RadarAltM, 1);
        Assert.Equal(150f, sweep.GateSpacingM, 1);

        var present = sweep.Data.Where(v => !float.IsNaN(v)).ToArray();
        Assert.Equal(82_596, present.Length);
        Assert.Equal(-22.0, present.Min(), 3);
        Assert.Equal(57.0, present.Max(), 3);
    }

    /// <summary>
    /// 592 gates of 150 m reaches 88.8 km, which is the 90 km these products are advertised at
    /// rounded to the nearest ten. A spacing wrong by a factor would put the whole field in
    /// the wrong place, and this is the arithmetic that would catch it.
    /// </summary>
    [Fact]
    public void TheSweepReachesTheProductsStatedRange()
    {
        var sweep = TdwrRadar.ToSweep(Z.Value, Moment.Reflectivity, 1288);
        double rangeKm = (sweep.FirstGateM + sweep.GateCount * sweep.GateSpacingM) / 1000.0;

        Assert.InRange(rangeKm, 85, 92);
    }

    /// <summary>
    /// <c>ScaleInfo</c> runs the opposite way from the threshold encoding — value =
    /// (raw - Offset) / Scale — so the conversion has to invert it. For the standard
    /// reflectivity encoding that is the familiar (level - 66) / 2.
    /// </summary>
    [Fact]
    public void TheScaleInvertsTheThresholdEncoding()
    {
        var sweep = TdwrRadar.ToSweep(Z.Value, Moment.Reflectivity, 1288);

        Assert.Equal(2f, sweep.Scale.Scale, 4);
        Assert.Equal(66f, sweep.Scale.Offset, 4);

        // And it round-trips: MetPy maps raw 128 to 31 dBZ.
        Assert.Equal(31.0, (128 - sweep.Scale.Offset) / sweep.Scale.Scale, 4);
    }

    /// <summary>
    /// Level 1 is a range-folded gate on the velocity products and unused on reflectivity —
    /// MetPy draws the same distinction with its range_fold flag. Marking reflectivity gates
    /// as folded would have the dealiasing machinery try to correct a field that never folds.
    /// </summary>
    [Fact]
    public void OnlyVelocityMarksRangeFoldedGates()
    {
        var velocity = TdwrRadar.ToSweep(V.Value, Moment.Velocity, 1288);
        var reflectivity = TdwrRadar.ToSweep(Z.Value, Moment.Reflectivity, 1288);

        int foldedV = 0, foldedZ = 0;
        for (int i = 0; i < velocity.Data.Length; i++) if (velocity.RangeFoldedMask[i]) foldedV++;
        for (int i = 0; i < reflectivity.Data.Length; i++) if (reflectivity.RangeFoldedMask[i]) foldedZ++;

        Assert.True(foldedV > 0, "a velocity product over a storm should carry folded gates");
        Assert.Equal(0, foldedZ);
    }

    /// <summary>
    /// Radials are evenly spaced here where a Level II cut's are not, so this is worth
    /// pinning: the renderer reads the azimuth of each radial rather than assuming a step,
    /// and the conversion must hand it real azimuths rather than indices.
    /// </summary>
    [Fact]
    public void AzimuthsAreRealBearingsCoveringTheCircle()
    {
        var sweep = TdwrRadar.ToSweep(Z.Value, Moment.Reflectivity, 1288);

        Assert.Equal(333.2f, sweep.AzimuthsDeg[0], 1);      // MetPy: start_az[0]
        Assert.Equal(332.2f, sweep.AzimuthsDeg[^1], 1);     // MetPy: start_az[-1]
        Assert.All(sweep.AzimuthsDeg, a => Assert.InRange(a, 0f, 360f));

        // 360 radials one degree apart wrap exactly once round.
        var sorted = sweep.AzimuthsDeg.OrderBy(a => a).ToArray();
        Assert.Equal(360, sorted.Length);
        Assert.InRange(sorted[^1] - sorted[0], 358, 360);
    }

    [Theory]
    [InlineData(180, true)]
    [InlineData(182, true)]
    [InlineData(187, true)]
    [InlineData(94, false)]    // WSR-88D digital reflectivity
    [InlineData(134, false)]   // digital VIL
    public void TdwrProductCodesAreRecognised(int code, bool expected) =>
        Assert.Equal(expected, TdwrRadar.IsTdwrProduct(code));
}
