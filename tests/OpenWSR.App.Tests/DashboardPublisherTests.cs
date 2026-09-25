using OpenWSR.App;
using OpenWSR.Ingest;

namespace OpenWSR.App.Tests;

/// <summary>
/// What the LAN dashboard is handed. Every point is the Norman, Oklahoma fixture or an
/// offset from it.
/// </summary>
[Collection("units")]
public class DashboardPublisherTests : IDisposable
{
    private const double Lat = 35.2226, Lon = -97.4395;
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 20, 0, 0, TimeSpan.Zero);

    private readonly UnitSystem _original = Units.System;

    public DashboardPublisherTests() => Units.System = UnitSystem.Imperial;

    public void Dispose() => Units.System = _original;

    [Fact]
    public void Places_keep_their_order_and_only_the_first_is_primary()
    {
        var snapshot = Build(places:
        [
            new WatchedPlace("Home", Lat, Lon, 40),
            new WatchedPlace("Office", Lat + 0.2, Lon, 15),
        ]);

        Assert.Equal(["Home", "Office"], snapshot.Places.Select(p => p.Name));
        Assert.True(snapshot.Places[0].IsPrimary);
        Assert.False(snapshot.Places[1].IsPrimary);
        Assert.Equal(15, snapshot.Places[1].RadiusKm);
    }

    [Fact]
    public void Threats_carry_the_panel_text_and_a_lower_case_rank()
    {
        var threat = new Threat("storm:A1", "Storm A1 approaching your area", "Storm A1",
            "moving NE at 35 mph", ThreatRank.Direct, 20, 12, Lat + 0.1, Lon - 0.1);

        var row = Assert.Single(Build(threats: [threat]).Threats);

        Assert.Equal("direct", row.Rank);
        Assert.Equal(threat.Range, row.Range);
        Assert.Equal("Storm A1", row.Label);
        Assert.True(row.Interrupts);
    }

    [Fact]
    public void A_glancing_pass_is_listed_but_does_not_interrupt()
    {
        var threat = new Threat("storm:B2", "t", "Storm B2", "d", ThreatRank.Glancing, 40, 30, Lat, Lon, PassesSide: "north");
        Assert.False(Assert.Single(Build(threats: [threat]).Threats).Interrupts);
    }

    [Fact]
    public void Alerts_are_lat_lon_pairs_in_nws_colours()
    {
        var alert = Alert("Tornado Warning", Lat + 0.1, Lon);

        var row = Assert.Single(Build(alerts: [alert]).Alerts);

        Assert.Equal("#ff2d2d", row.Color);
        Assert.Equal([Lat + 0.1, Lon - 0.1], row.Polygons[0][0]);
    }

    [Fact]
    public void Alerts_far_from_every_place_are_left_out()
    {
        var near = Alert("Severe Thunderstorm Warning", Lat + 1, Lon);          // ~110 km
        var far = Alert("Flash Flood Warning", 44.0, -71.0);                     // New England

        var snapshot = Build(alerts: [near, far]);

        Assert.Equal(["Severe Thunderstorm Warning"], snapshot.Alerts.Select(a => a.Event));
    }

    [Fact]
    public void With_no_place_saved_every_alert_goes_out()
    {
        var snapshot = Build(places: [], alerts: [Alert("Flash Flood Warning", 44.0, -71.0)]);
        Assert.Single(snapshot.Alerts);
    }

    [Fact]
    public void Storm_motion_is_formatted_and_an_untracked_cell_says_nothing()
    {
        var tracked = Storm("A1", speedKmh: 56.3, bearingDeg: 45);
        var untracked = Storm("B2", speedKmh: null, bearingDeg: null);

        var storms = Build(stormSite: "KTLX", storms: [tracked, untracked]).Storms;

        Assert.Equal("NE 35 mph", storms[0].Motion);
        Assert.Null(storms[1].Motion);
        Assert.Equal([Lat + 0.05, Lon + 0.05], storms[0].ForecastPath[0]);
    }

    [Fact]
    public void Storm_clock_is_null_when_no_watch_is_armed()
    {
        // A stale refresh time left over from a disarmed watch would read as "storms current".
        var snapshot = Build(stormSite: null, stormsUpdated: Now);
        Assert.Null(snapshot.StormsUpdatedUtc);
        Assert.Null(snapshot.StormSite);
    }

    [Theory]
    [InlineData("8765", 8765)]
    [InlineData(" 8080 ", 8080)]
    [InlineData("1024", 1024)]
    [InlineData("65535", 65535)]
    [InlineData("80", null)]
    [InlineData("65536", null)]
    [InlineData("-1", null)]
    [InlineData("88a", null)]
    [InlineData("", null)]
    public void Ports_are_checked_before_they_are_saved(string text, int? expected) =>
        Assert.Equal(expected, DashboardAddresses.ParsePort(text));

    [Fact]
    public async Task Overlapping_applies_leave_one_server_on_the_last_port()
    {
        // Startup and a Settings save can both apply at once. Unserialised, the second saw the
        // first still binding, started its own, and leaked a listener on the first port.
        var (first, second) = TwoFreePorts();
        await using var publisher = new DashboardPublisher();

        await Task.WhenAll(publisher.ApplyAsync(true, first), publisher.ApplyAsync(true, second));

        Assert.True(publisher.IsRunning);
        Assert.Equal(second, publisher.Port);
        Assert.True(IsFree(first), "the first server was left listening");

        await publisher.ApplyAsync(false, second);
        Assert.False(publisher.IsRunning);
        Assert.True(IsFree(second));
    }

    /// <summary>
    /// Two ports, held open together while they are chosen so the OS cannot hand back the
    /// same one twice — which it will, and which would make this test report a leak that is
    /// not there.
    /// </summary>
    private static (int, int) TwoFreePorts()
    {
        var a = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Any, 0) { Server = { DualMode = true } };
        var b = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Any, 0) { Server = { DualMode = true } };
        a.Start();
        b.Start();
        int first = ((System.Net.IPEndPoint)a.LocalEndpoint).Port;
        int second = ((System.Net.IPEndPoint)b.LocalEndpoint).Port;
        a.Stop();
        b.Stop();
        return (first, second);
    }

    private static bool IsFree(int port)
    {
        try
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Any, port);
            probe.Server.DualMode = true;
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    [Fact]
    public void Dashboard_is_off_until_switched_on()
    {
        var settings = new AppSettings();
        Assert.False(settings.DashboardEnabled);
        Assert.Equal(DashboardAddresses.DefaultPort, settings.DashboardPort);
    }

    private static Dashboard.DashboardSnapshot Build(
        IReadOnlyList<WatchedPlace>? places = null,
        IReadOnlyList<Threat>? threats = null,
        IReadOnlyList<ActiveAlert>? alerts = null,
        string? stormSite = null,
        IReadOnlyList<TrackedStorm>? storms = null,
        DateTimeOffset? stormsUpdated = null) =>
        DashboardPublisher.Build(
            Now,
            places ?? [new WatchedPlace("Home", Lat, Lon, 40)],
            threats ?? [],
            alerts ?? [],
            Now,
            stormSite,
            storms ?? [],
            stormsUpdated ?? Now);

    private static ActiveAlert Alert(string eventName, double lat, double lon) => new(
        $"urn:{eventName}", eventName, eventName, "Severe", Now.AddMinutes(30), "", null,
        [[(lat, lon - 0.1), (lat, lon + 0.1), (lat - 0.1, lon), (lat, lon - 0.1)]]);

    private static TrackedStorm Storm(string id, double? speedKmh, double? bearingDeg) => new(
        id, Lat, Lon,
        [(Lat + 0.05, Lon + 0.05)], [(Lat - 0.05, Lon - 0.05)],
        speedKmh, bearingDeg, 40, 10, 1, null, null, null, null);
}
