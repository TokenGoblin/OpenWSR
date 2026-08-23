using OpenWSR.App;
using OpenWSR.Ingest;

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

    // ---- the live set: what the side list draws ----
    //
    // These are the half of the contract the tray notification does not cover. A threat that
    // is still there on the next poll must stay on the list without alerting a second time,
    // and the two halves refresh on different clocks, so neither may erase the other.

    private static ActiveAlert Alert(
        string id, string eventName, (double LatDeg, double LonDeg)[] ring,
        DateTimeOffset? expires = null, string severity = "Severe") =>
        new(id, eventName, $"{eventName} headline", severity, expires, "", null, [ring]);

    /// <summary>A box roughly 0.4° a side centred on a point — about 44 km across at the equator.</summary>
    private static (double LatDeg, double LonDeg)[] Box(double latDeg, double lonDeg, double halfDeg = 0.2) =>
        [(latDeg - halfDeg, lonDeg - halfDeg), (latDeg - halfDeg, lonDeg + halfDeg),
         (latDeg + halfDeg, lonDeg + halfDeg), (latDeg + halfDeg, lonDeg - halfDeg)];

    [Fact]
    public void TheListKeepsAThreatThatTheThrottleStopsRenotifying()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        var raised = new List<Threat>();
        monitor.ThreatDetected += raised.Add;

        var storm = Storm(0, -0.2, [(0, -0.1), (0, 0.0)]);
        monitor.EvaluateStorms([storm]);
        monitor.EvaluateStorms([storm]);
        monitor.EvaluateStorms([storm]);

        Assert.Single(raised);              // interrupted once
        Assert.Single(monitor.Current);     // still on screen
    }

    [Fact]
    public void AStormThatMovesOffLeavesTheList()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);

        monitor.EvaluateStorms([Storm(0, -0.2, [(0, -0.1), (0, 0.0)])]);
        Assert.Single(monitor.Current);

        // The next volume no longer carries that cell at all.
        monitor.EvaluateStorms([]);
        Assert.Empty(monitor.Current);
    }

    [Fact]
    public void TheTwoHalvesRefreshIndependently()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);

        monitor.EvaluateWarnings([Alert("w1", "Severe Thunderstorm Warning", Box(0, 0))]);
        Assert.Single(monitor.Current);

        // A storm poll landing in between must not wipe the warning, and vice versa: they
        // run on 2-minute and 60-second timers respectively.
        monitor.EvaluateStorms([Storm(0, -0.2, [(0, -0.1), (0, 0.0)])]);
        Assert.Equal(2, monitor.Current.Count);

        monitor.EvaluateWarnings([]);
        Assert.Single(monitor.Current);
        Assert.StartsWith("storm:", monitor.Current[0].Key);
    }

    [Fact]
    public void TheWorstThingIsAtTheTop()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 60);

        // A cell 20 minutes out, a severe warning sitting over home, and a tornado warning.
        monitor.EvaluateStorms([Storm(0, -0.35, [(0, -0.2), (0, -0.1), (0, 0.0)], id: "B2")]);
        monitor.EvaluateWarnings([
            Alert("w1", "Severe Thunderstorm Warning", Box(0, 0)),
            Alert("w2", "Tornado Warning", Box(0.3, 0)),
        ]);

        Assert.Equal(3, monitor.Current.Count);
        Assert.Equal(ThreatRank.Tornadic, monitor.Current[0].Rank);
        Assert.Equal("warn:w2", monitor.Current[0].Key);
        Assert.Equal(ThreatRank.Overhead, monitor.Current[1].Rank);
        Assert.Equal("storm:B2", monitor.Current[2].Key);
    }

    /// <summary>
    /// The ordering that soonest-first got wrong on live data. Y5's closest approach is where
    /// it already is, so its ETA is zero; V1 is closing to a third of that distance over the
    /// next twenty minutes. V1 is the one worth reading first.
    /// </summary>
    [Fact]
    public void ClosestComesFirstEvenWhenSomethingElseIsAlreadyAtItsNearest()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 80);

        var alreadyThere = Storm(0, 0.35, [(0, 0.5), (0, 0.7)], id: "Y5");   // ~39 km, receding
        var closingIn = Storm(0.13, -0.35, [(0.13, -0.15), (0.13, 0.0)], id: "V1"); // ~14 km at pass

        monitor.EvaluateStorms([alreadyThere, closingIn]);

        Assert.Equal("storm:V1", monitor.Current[0].Key);
        Assert.Equal(0, monitor.Current[1].EtaMinutes);
        Assert.True(monitor.Current[0].EtaMinutes > 0);
    }

    /// <summary>
    /// The warnings layer draws statements and advisories when they are ticked, and it should.
    /// The threat list is a different claim: it is the list of things coming for you.
    /// </summary>
    [Fact]
    public void AdvisoriesAndStatementsAreNotThreats()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        var raised = new List<Threat>();
        monitor.ThreatDetected += raised.Add;

        monitor.EvaluateWarnings([
            Alert("w1", "Special Weather Statement", Box(0, 0), severity: "Moderate"),
            Alert("w2", "Beach Hazards Statement", Box(0, 0), severity: "Minor"),
            Alert("w3", "Severe Thunderstorm Warning", Box(0, 0)),
        ]);

        Assert.Single(monitor.Current);
        Assert.Equal("warn:w3", monitor.Current[0].Key);
        Assert.Single(raised);
    }

    /// <summary>A watch is not a warning, but an Extreme one still belongs on the list.</summary>
    [Fact]
    public void ASevereNonWarningStillCounts()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);

        monitor.EvaluateWarnings([
            Alert("w1", "Particularly Dangerous Situation", Box(0, 0), severity: "Extreme"),
        ]);

        Assert.Single(monitor.Current);
    }

    [Fact]
    public void ExpiredWarningsDropOffWithoutWaitingForThePoll()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        var now = new DateTimeOffset(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);

        monitor.EvaluateWarnings([
            Alert("w1", "Tornado Warning", Box(0, 0), expires: now.AddMinutes(10)),
            Alert("w2", "Severe Thunderstorm Warning", Box(0, 0), expires: now.AddMinutes(-1)),
        ]);
        Assert.Equal(2, monitor.Current.Count);

        monitor.ExpireStale(now);
        Assert.Single(monitor.Current);
        Assert.Equal("warn:w1", monitor.Current[0].Key);
    }

    [Fact]
    public void MovingHomeBlanksTheListRatherThanLeavingStaleRanges()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        monitor.EvaluateWarnings([Alert("w1", "Severe Thunderstorm Warning", Box(0, 0))]);
        Assert.Single(monitor.Current);

        // Every distance in that list was measured against the old home.
        monitor.Configure(45, -100, radiusKm: 40);
        Assert.Empty(monitor.Current);
    }

    [Fact]
    public void ClearingHomeEmptiesTheList()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        monitor.EvaluateStorms([Storm(0, -0.2, [(0, -0.1), (0, 0.0)])]);
        Assert.Single(monitor.Current);

        monitor.Configure(null, null, radiusKm: 40);
        monitor.EvaluateStorms([Storm(0, -0.2, [(0, -0.1), (0, 0.0)])]);

        Assert.False(monitor.IsArmed);
        Assert.Empty(monitor.Current);
    }

    [Fact]
    public void AWarningReportsNoEtaBecauseAPolygonIsNotATrack()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        monitor.EvaluateWarnings([Alert("w1", "Severe Thunderstorm Warning", Box(0, 0))]);

        Assert.Null(monitor.Current[0].EtaMinutes);
        Assert.Equal("over you", monitor.Current[0].Range);
    }

    [Fact]
    public void ChangesArePublishedToTheListSubscriber()
    {
        var monitor = new ThreatMonitor();
        monitor.Configure(0, 0, radiusKm: 40);
        var published = new List<int>();
        monitor.ThreatsChanged += list => published.Add(list.Count);

        monitor.EvaluateStorms([Storm(0, -0.2, [(0, -0.1), (0, 0.0)])]);
        monitor.EvaluateWarnings([Alert("w1", "Tornado Warning", Box(0, 0))]);

        Assert.Equal([1, 2], published);
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
