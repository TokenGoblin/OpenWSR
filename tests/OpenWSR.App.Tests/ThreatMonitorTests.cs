using OpenWSR.App;

namespace OpenWSR.App.Tests;

/// <summary>
/// Proximity alerting: the one feature where a false negative matters. These assert the
/// track geometry and the once-per-hour throttle that stops a stationary storm from
/// alerting every two minutes.
/// </summary>
public class ThreatMonitorTests
{
    /// <summary>SCIT forecast points are 15 minutes apart, index 0 being the current position.</summary>
    private static TrackedStorm Storm(
        double latDeg, double lonDeg,
        (double LatDeg, double LonDeg)[] forecast,
        string id = "A1",
        int posh = 0,
        double? mesoRadiusKm = null) =>
        new(id, latDeg, lonDeg, forecast, [], SpeedKmh: 50, BearingDeg: 90,
            ProbabilityOfHail: 0, ProbabilityOfSevereHail: posh, MaxHailSizeInches: 0,
            MesoRadiusKm: mesoRadiusKm, MaxDbz: null, CellBasedVil: null, EchoTopKft: null);

    [Fact]
    public void ClosestApproachFindsThePassingPointNotTheEndpoints()
    {
        // A storm at (0, -1) heading due east past a home at (0.05, 0). It starts ~111 km
        // west and ends ~111 km east; the closest it ever gets is the 0.05° offset (~5.5 km).
        var storm = Storm(0, -1.0, [(0, -0.5), (0, 0.0), (0, 0.5), (0, 1.0)]);

        var approach = ThreatMonitor.ClosestApproach(storm, 0.05, 0.0);

        Assert.NotNull(approach);
        Assert.InRange(approach!.Value.DistanceKm, 5.0, 6.5);
        // Two forecast steps in — 15 minutes each — puts the pass at about half an hour.
        Assert.InRange(approach.Value.EtaMinutes, 28, 32);
    }

    [Fact]
    public void ClosestApproachIsZeroMinutesWhenTheStormIsAlreadyOverhead()
    {
        var storm = Storm(10, 20, [(10.5, 20), (11, 20)]);
        var approach = ThreatMonitor.ClosestApproach(storm, 10, 20);

        Assert.NotNull(approach);
        Assert.InRange(approach!.Value.DistanceKm, 0, 0.001);
        Assert.Equal(0, approach.Value.EtaMinutes);
    }

    [Fact]
    public void StormOutsideTheRadiusRaisesNothing()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        var raised = new List<Threat>();
        monitor.ThreatDetected += raised.Add;

        // Ten degrees away and moving further east: never within 40 km.
        monitor.EvaluateStorms([Storm(0, 10, [(0, 10.5), (0, 11)])]);

        Assert.Empty(raised);
    }

    [Fact]
    public void StormCrossingTheRadiusRaisesOnceWithAnEta()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        var raised = new List<Threat>();
        monitor.ThreatDetected += raised.Add;

        var storm = Storm(0, -0.5, [(0, -0.25), (0, 0.0), (0, 0.25)]);
        monitor.EvaluateStorms([storm]);
        Assert.Single(raised);
        Assert.Contains("approaching", raised[0].Title);

        // The same cell on the next 2-minute refresh must not alert again.
        monitor.EvaluateStorms([storm]);
        Assert.Single(raised);
    }

    [Fact]
    public void MesocycloneIsFlaggedAsUrgent()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        Threat? got = null;
        monitor.ThreatDetected += t => got = t;

        monitor.EvaluateStorms([Storm(0, -0.2, [(0, 0.0)], mesoRadiusKm: 3.5)]);

        Assert.NotNull(got);
        Assert.True(got!.IsTornado, "a detected mesocyclone should raise the urgent notification");
    }

    [Fact]
    public void MovingHomeReEvaluatesEverything()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        var raised = new List<Threat>();
        monitor.ThreatDetected += raised.Add;

        var storm = Storm(0, -0.5, [(0, -0.25), (0, 0.0)]);
        monitor.EvaluateStorms([storm]);
        Assert.Single(raised);

        // Re-siting home clears the throttle: this is a different question being asked.
        monitor.Configure(0.01, 0, radiusKm: 40);
        monitor.EvaluateStorms([storm]);
        Assert.Equal(2, raised.Count);
    }

    [Fact]
    public void UnarmedMonitorNeverRaises()
    {
        var monitor = new ThreatMonitor();
        var raised = new List<Threat>();
        monitor.ThreatDetected += raised.Add;

        Assert.False(monitor.IsArmed);
        monitor.EvaluateStorms([Storm(0, 0, [(0, 0)])]);
        Assert.Empty(raised);
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(45, "NE")]
    [InlineData(90, "E")]
    [InlineData(180, "S")]
    [InlineData(270, "W")]
    [InlineData(350, "N")]  // wraps back to north rather than indexing off the end
    [InlineData(359.9, "N")]
    public void CompassPointsWrapCleanly(double bearing, string expected) =>
        Assert.Equal(expected, ThreatMonitor.CompassPoint(bearing));
}
