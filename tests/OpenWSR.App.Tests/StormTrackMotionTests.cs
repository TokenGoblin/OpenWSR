using OpenWSR.App;
using OpenWSR.Geo;

namespace OpenWSR.App.Tests;

/// <summary>
/// Storm motion from a SCIT track. Both of the cases here were live bugs: cells with no
/// usable track reported "moving north at 0 mph", and cells falling back to a past position
/// reported about a third of their real speed.
/// </summary>
public sealed class StormTrackMotionTests
{
    private static (double, double) Home => (35.0, -97.0);

    /// <summary>A point a given distance north of home, so the expected bearing is 0°.</summary>
    private static (double LatDeg, double LonDeg) NorthOf(double km) =>
        GeoMath.Offset(35.0, -97.0, 0.0, km * 1000.0);

    [Fact]
    public void AForecastLegGivesSpeedOverFifteenMinutes()
    {
        // 10 km in 15 minutes is 40 km/h, whatever the VCP says — the forecast step is fixed.
        var m = StormTrackMotion.Derive(Home, [NorthOf(10)], [], vcp: 212);

        Assert.NotNull(m);
        Assert.Equal(40.0, m!.Value.SpeedKmh, 1);
        Assert.Equal(0.0, m.Value.BearingDeg, 1);
    }

    [Fact]
    public void APastLegIsOneVolumeScan_NotFifteenMinutes()
    {
        // Same 10 km, but one VCP 212 scan back — 4.5 minutes, so 133 km/h, not 40.
        var m = StormTrackMotion.Derive(Home, [], [NorthOf(10)], vcp: 212);

        Assert.NotNull(m);
        Assert.Equal(10.0 * (60.0 / 4.5), m!.Value.SpeedKmh, 1);
        Assert.True(m.Value.SpeedKmh > 130);
    }

    [Fact]
    public void APastLegPointsAwayFromWhereTheStormWas()
    {
        // The cell was north of here and is here now, so it is travelling south.
        var m = StormTrackMotion.Derive(Home, [], [NorthOf(10)], vcp: 212);

        Assert.Equal(180.0, m!.Value.BearingDeg, 1);
    }

    [Fact]
    public void TheScanTimeOfTheVcpActuallyChangesTheAnswer()
    {
        var fast = StormTrackMotion.Derive(Home, [], [NorthOf(10)], vcp: 212);   // 4.5 min
        var slow = StormTrackMotion.Derive(Home, [], [NorthOf(10)], vcp: 31);    // 10 min

        Assert.True(fast!.Value.SpeedKmh > slow!.Value.SpeedKmh * 2);
    }

    [Fact]
    public void AForecastPointOnTheCellItselfIsNotMotion()
    {
        // What the algorithm emits for a cell it has only just picked up. Reporting 0 mph
        // here claims the storm was measured to be stationary, and reporting a bearing of 0°
        // claims it was measured to be heading north.
        var m = StormTrackMotion.Derive(Home, [(35.0, -97.0)], [], vcp: 212);

        Assert.Null(m);
    }

    [Fact]
    public void ACellWithNoTrackAtAllHasNoMotion() =>
        Assert.Null(StormTrackMotion.Derive(Home, [], [], vcp: 212));

    [Fact]
    public void ADegenerateForecastFallsThroughToAUsablePastLeg()
    {
        // Preferring the forecast must not mean preferring a placeholder over real history.
        var m = StormTrackMotion.Derive(Home, [(35.0, -97.0)], [NorthOf(10)], vcp: 212);

        Assert.NotNull(m);
        Assert.Equal(180.0, m!.Value.BearingDeg, 1);
    }

    [Fact]
    public void AForecastLegWinsOverAPastOne()
    {
        // Both usable: the forecast is the better estimate and is on a known clock.
        var m = StormTrackMotion.Derive(Home, [NorthOf(10)], [GeoMath.Offset(35.0, -97.0, Math.PI, 10_000)], vcp: 212);

        Assert.Equal(40.0, m!.Value.SpeedKmh, 1);
        Assert.Equal(0.0, m.Value.BearingDeg, 1);
    }
}
