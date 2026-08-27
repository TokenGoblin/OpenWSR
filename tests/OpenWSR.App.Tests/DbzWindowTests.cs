namespace OpenWSR.App.Tests;

/// <summary>
/// The dBZ window's default is a product decision with a physical justification, so both are
/// pinned here rather than left in a comment that can drift from the number beside it.
/// </summary>
public sealed class DbzWindowTests
{
    /// <summary>
    /// Rain rate in mm/hr for a reflectivity, by Marshall-Palmer — <c>Z = 200·R^1.6</c>, the
    /// relation NEXRAD's own precipitation products assume.
    /// </summary>
    private static double RainRateMmHr(double dbz) =>
        Math.Pow(Math.Pow(10, dbz / 10) / 200.0, 1 / 1.6);

    [Fact]
    public void TheWindowOpensWhereRainStarts()
    {
        var settings = new AppSettings();

        Assert.Equal(20f, settings.DbzFilterMin);
        Assert.Equal(75f, settings.DbzFilterMax);
    }

    [Fact]
    public void TwentyDbzIsLightRainAndTenIsNotRainAtAll()
    {
        // The justification for the default, as a fact rather than a comment. 20 dBZ is
        // 0.026 in/hr — light rain reaching the ground. 10 dBZ is 0.006, which is insects.
        Assert.Equal(0.026, RainRateMmHr(20) / 25.4, 3);
        Assert.Equal(0.006, RainRateMmHr(10) / 25.4, 3);

        // And the gap between them is a factor of four, which is why a floor between the two
        // separates weather from clutter at all.
        Assert.True(RainRateMmHr(20) / RainRateMmHr(10) > 4);
    }

    [Fact]
    public void TheDefaultAgreesWithTheMaskChosenSeparately()
    {
        // GateQuality picked 20 dBZ from a measurement of where velocity stops meaning
        // anything — a different question, arrived at independently, same answer. If one
        // moves, the other is worth revisiting.
        Assert.Equal(
            OpenWSR.Nexrad.Analysis.GateQuality.DefaultMinReflectivityDbz,
            new AppSettings().DbzFilterMin);
    }

    [Fact]
    public void TheFloorCanStillBeLoweredForSnow()
    {
        // Snow returns far less energy per unit water, so 15 dBZ of it can be accumulating
        // steadily. The default hides that, which is only acceptable because it is a floor
        // the user can move — the range has to reach below it.
        var settings = new AppSettings { DbzFilterMin = 5f };

        Assert.True(settings.DbzFilterMin < 15f, "the window must reach below snow's range");
        Assert.True(RainRateMmHr(15) < RainRateMmHr(20));
    }
}
