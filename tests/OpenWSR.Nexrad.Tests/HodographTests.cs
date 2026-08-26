using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Storm-relative helicity checked against <b>MetPy 1.7.1</b>
/// (<c>metpy.calc.storm_relative_helicity</c>) on two profiles: a smooth veering one on a
/// regular height grid, and an irregular one whose layer tops fall between levels, which is
/// what a real VAD profile looks like and where the interpolation has to be right.
/// </summary>
public sealed class HodographTests
{
    /// <summary>A level from speed and meteorological direction, as the VAD fit reports them.</summary>
    private static VadLevel Level(double altitudeM, double directionDeg, double speedMs) =>
        new(altitudeM, speedMs, directionDeg, RmsMs: 0.5, Samples: 400, ElevationDeg: 0.5);

    /// <summary>Veers 150° to 260° and 5 to 30 m/s by 6 km, then constant. 250 m spacing.</summary>
    private static IReadOnlyList<HodographPoint> Smooth()
    {
        var levels = new List<VadLevel>();
        for (double z = 0; z <= 8000; z += 250)
        {
            double f = Math.Clamp(z / 6000.0, 0, 1);
            levels.Add(Level(z, 150 + 110 * f, 5 + 25 * f));
        }
        return Hodograph.FromProfile(levels);
    }

    /// <summary>Irregular levels starting at 120 m — a real profile's shape.</summary>
    private static IReadOnlyList<HodographPoint> Irregular()
    {
        double[] z = [120, 430, 780, 1150, 1620, 2100, 2680, 3310, 4050, 4900, 5800, 6900];
        double[] direction = [160, 172, 185, 198, 210, 221, 233, 244, 252, 258, 262, 265];
        double[] speed = [6, 9, 12, 14, 17, 19, 22, 25, 27, 29, 31, 33];
        return Hodograph.FromProfile(
            [.. z.Select((_, i) => Level(z[i], direction[i], speed[i]))]);
    }

    [Fact]
    public void ComponentsPointWhereTheAirIsGoing()
    {
        // A wind *from* 270 (a westerly) blows toward the east: u positive, v about zero.
        // The sign here is the whole trap — get it wrong and the hodograph is simply
        // rotated 180 degrees, which still looks like a hodograph.
        var westerly = Hodograph.FromProfile([Level(1000, 270, 10)])[0];
        Assert.Equal(10, westerly.UMs, 6);
        Assert.Equal(0, westerly.VMs, 6);

        // A wind *from* the south blows north.
        var southerly = Hodograph.FromProfile([Level(1000, 180, 10)])[0];
        Assert.Equal(0, southerly.UMs, 6);
        Assert.Equal(10, southerly.VMs, 6);
    }

    [Theory]
    // MetPy, smooth profile, storm motion (8, 6) m/s.
    [InlineData(1000, 49.683376)]
    [InlineData(3000, 166.367854)]
    public void HelicityMatchesMetPyOnARegularGrid(double depthM, double expected)
    {
        var (_, _, total) = Hodograph.StormRelativeHelicity(Smooth(), 8.0, 6.0, depthM);
        Assert.Equal(expected, total, 4);
    }

    [Theory]
    // MetPy, irregular profile based at 120 m, so the layer tops land between levels.
    [InlineData(1000, 8.0, 6.0, 94.105853)]
    [InlineData(1000, 0.0, 0.0, 70.249524)]
    [InlineData(3000, 8.0, 6.0, 254.436089)]
    [InlineData(3000, 0.0, 0.0, 344.629981)]
    public void HelicityMatchesMetPyWhenTheLayerTopFallsBetweenLevels(
        double depthM, double stormU, double stormV, double expected)
    {
        var (_, _, total) = Hodograph.StormRelativeHelicity(Irregular(), stormU, stormV, depthM);
        Assert.Equal(expected, total, 4);
    }

    [Fact]
    public void DepthIsMeasuredFromTheLowestFittedLevel()
    {
        // MetPy's with_agl=True. The lowest level a VAD fit finds moves between volumes, so
        // measuring from sea level would make "0-1 km" mean something different every scan.
        var layer = Hodograph.Layer(Irregular(), 1000);

        Assert.Equal(120, layer[0].AltitudeM, 6);
        Assert.Equal(1120, layer[^1].AltitudeM, 6);   // 120 m base + 1000 m
    }

    [Fact]
    public void TheLayerTopIsInterpolatedNotSnapped()
    {
        // MetPy's layer for 0-1000 AGL ends at u = 4.06026, v = 13.20448 — between the
        // 780 m and 1150 m levels. Snapping to 780 would drop a third of the layer.
        var layer = Hodograph.Layer(Irregular(), 1000);

        Assert.Equal(4, layer.Count);
        Assert.Equal(4.06026, layer[^1].UMs, 4);
        Assert.Equal(13.20448, layer[^1].VMs, 4);
    }

    [Fact]
    public void PositiveAndNegativeAreReportedApartBecauseTheyCancel()
    {
        // A hodograph that doubles back contains rotation of both signs. Reporting only the
        // total would call this benign when it is not.
        var levels = new List<VadLevel>
        {
            Level(0, 180, 10),
            Level(1000, 270, 10),   // veering: positive
            Level(2000, 180, 10),   // backing again: negative
        };

        var (positive, negative, total) =
            Hodograph.StormRelativeHelicity(Hodograph.FromProfile(levels), 0, 0, 3000);

        Assert.True(positive > 0, "the veering half should contribute positively");
        Assert.True(negative < 0, "the backing half should contribute negatively");
        Assert.Equal(positive + negative, total, 9);
        Assert.True(Math.Abs(total) < positive, "they should cancel, which is the point");
    }

    [Fact]
    public void AStraightHodographHasNoHelicity()
    {
        // Unidirectional shear: speed increases, direction does not turn. Nothing to tilt.
        var levels = new List<VadLevel>();
        for (double z = 0; z <= 3000; z += 500) levels.Add(Level(z, 240, 5 + z / 150.0));

        var (_, _, total) =
            Hodograph.StormRelativeHelicity(Hodograph.FromProfile(levels), 0, 0, 3000);

        Assert.Equal(0, total, 6);
    }

    [Fact]
    public void BulkShearIsTheVectorDifferenceAcrossTheLayer()
    {
        var shear = Hodograph.BulkShear(Smooth(), 6000);

        // Surface wind is from 150 at 5 m/s, the 6 km wind from 260 at 30.
        var points = Smooth();
        var top = points.First(p => Math.Abs(p.AltitudeM - 6000) < 1e-6);
        Assert.Equal(top.UMs - points[0].UMs, shear.UMs, 6);
        Assert.Equal(top.VMs - points[0].VMs, shear.VMs, 6);
        Assert.Equal(Math.Sqrt(shear.UMs * shear.UMs + shear.VMs * shear.VMs), shear.MagnitudeMs, 9);
    }

    [Fact]
    public void AskingForMoreDepthThanExistsReturnsWhatThereIs()
    {
        // Right for a calculation, a trap for a caption: a profile fitted only to 2.7 km
        // yields a "0-6 km shear" that is nothing of the sort unless the caller checks.
        var shallow = Hodograph.FromProfile(
        [
            Level(300, 200, 8),
            Level(1400, 230, 14),
            Level(2600, 250, 19),
        ]);

        Assert.Equal(2300, Hodograph.DepthM(shallow), 6);

        var layer = Hodograph.Layer(shallow, 6000);
        Assert.Equal(3, layer.Count);
        Assert.Equal(2600, layer[^1].AltitudeM, 6);   // not 6300
    }

    [Fact]
    public void AnEmptyProfileIsNotAnError()
    {
        // Clear air often fits nothing at all, and that is a normal Tuesday, not a fault.
        Assert.Empty(Hodograph.FromProfile([]));
        Assert.Empty(Hodograph.Layer([], 3000));
        Assert.Equal((0.0, 0.0, 0.0), Hodograph.StormRelativeHelicity([], 0, 0, 3000));
        Assert.Equal((0.0, 0.0), Hodograph.MeanWind([], 6000));
    }
}
