using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenWSR.Dashboard;

namespace OpenWSR.Dashboard.Tests;

/// <summary>
/// The server over real sockets, on a port the OS picks. Fixtures sit on the Norman,
/// Oklahoma point every location test in this repository uses.
/// </summary>
public sealed class DashboardServerTests : IAsyncLifetime
{
    private const double NormanLat = 35.2226, NormanLon = -97.4395;

    private readonly DashboardServer _server = new();
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        await _server.StartAsync(0);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Root_serves_the_page_and_its_assets()
    {
        var page = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("dashboard.js", html);

        // Everything the page references ships inside the assembly: a LAN with filtered
        // egress must still get its map library.
        foreach (var asset in new[] { "dashboard.js", "dashboard.css", "vendor/leaflet.js", "vendor/leaflet.css" })
        {
            var response = await _http.GetAsync(asset);
            Assert.True(response.IsSuccessStatusCode, asset);
            Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 100, asset);
        }
    }

    [Fact]
    public async Task Unknown_paths_are_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("nope.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("../OpenWSR.Dashboard.dll")).StatusCode);
    }

    [Fact]
    public async Task Page_may_be_framed_from_any_origin()
    {
        // The Home Assistant webpage card is an iframe on HA's own origin, which OpenWSR
        // cannot know in advance.
        var response = await _http.GetAsync("/");
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        Assert.Equal("frame-ancestors *", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task State_before_the_first_publish_is_empty_and_says_so()
    {
        using var json = JsonDocument.Parse(await _http.GetStringAsync("api/state"));
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("alertsUpdatedUtc").ValueKind);
        Assert.Equal(0, root.GetProperty("threats").GetArrayLength());
    }

    [Fact]
    public async Task State_carries_a_published_snapshot_in_camel_case()
    {
        _server.Publish(Sample());

        var response = await _http.GetAsync("api/state");
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var threat = root.GetProperty("threats")[0];
        Assert.Equal("tornadic", threat.GetProperty("rank").GetString());
        Assert.Equal("12.4 mi · ~9 min", threat.GetProperty("range").GetString());

        var ring = root.GetProperty("alerts")[0].GetProperty("polygons")[0];
        Assert.Equal(NormanLat + 0.1, ring[0][0].GetDouble(), 6);
        Assert.Equal(NormanLon - 0.1, ring[0][1].GetDouble(), 6);

        var place = root.GetProperty("places")[0];
        Assert.True(place.GetProperty("isPrimary").GetBoolean());
        Assert.Equal("KTLX", root.GetProperty("stormSite").GetString());
    }

    [Fact]
    public async Task An_unchanged_snapshot_revalidates_as_not_modified()
    {
        _server.Publish(Sample());
        var first = await _http.GetAsync("api/state");
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        var request = new HttpRequestMessage(HttpMethod.Get, "api/state");
        request.Headers.IfNoneMatch.Add(etag);
        var second = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);

        // A new snapshot is a new tag, so the next poll gets the body.
        _server.Publish(Sample() with { UpdatedUtc = Sample().UpdatedUtc.AddMinutes(1) });
        var third = new HttpRequestMessage(HttpMethod.Get, "api/state");
        third.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(third)).StatusCode);
    }

    [Fact]
    public async Task Slices_serve_one_part_each()
    {
        _server.Publish(Sample());
        using var threats = JsonDocument.Parse(await _http.GetStringAsync("api/threats"));
        using var alerts = JsonDocument.Parse(await _http.GetStringAsync("api/alerts"));
        using var storms = JsonDocument.Parse(await _http.GetStringAsync("api/storms"));
        Assert.Equal(1, threats.RootElement.GetArrayLength());
        Assert.Equal("Tornado Warning", alerts.RootElement[0].GetProperty("event").GetString());
        Assert.Equal("A1", storms.RootElement[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_taken_port_fails_with_a_message_a_person_can_act_on()
    {
        await using var second = new DashboardServer();
        var error = await Assert.ThrowsAsync<IOException>(() => second.StartAsync(_server.Port!.Value));
        Assert.Contains("already in use", error.Message);
        Assert.Contains("Settings", error.Message);
        Assert.False(second.IsRunning);
    }

    [Fact]
    public async Task Stop_releases_the_port()
    {
        var port = _server.Port!.Value;
        await _server.StopAsync();
        Assert.False(_server.IsRunning);

        await using var again = new DashboardServer();
        await again.StartAsync(port);
        Assert.Equal(port, again.Port);
    }

    private static DashboardSnapshot Sample()
    {
        var now = new DateTimeOffset(2026, 5, 20, 20, 0, 0, TimeSpan.Zero);
        return new DashboardSnapshot(
            UpdatedUtc: now,
            AlertsUpdatedUtc: now,
            StormsUpdatedUtc: now,
            StormSite: "KTLX",
            Places: [new DashboardPlace("Norman", NormanLat, NormanLon, 80, IsPrimary: true)],
            Threats:
            [
                new DashboardThreat("storm:A1", "Storm A1 approaching your area", "Cell A1", "Rotation detected",
                    "12.4 mi · ~9 min", "tornadic", Interrupts: true, 20, 9, NormanLat + 0.1, NormanLon - 0.1,
                    null, null),
            ],
            Alerts:
            [
                new DashboardAlert("urn:1", "Tornado Warning", "Tornado Warning until 3:45 PM", "Extreme", now.AddMinutes(45),
                    "#ff2d2d",
                    [[[NormanLat + 0.1, NormanLon - 0.1], [NormanLat + 0.1, NormanLon + 0.1], [NormanLat - 0.1, NormanLon]]]),
            ],
            Storms:
            [
                new DashboardStorm("A1", NormanLat + 0.1, NormanLon - 0.1,
                    [[NormanLat + 0.05, NormanLon - 0.05]], [[NormanLat + 0.15, NormanLon - 0.15]],
                    "NE 35 mph", 100, 70, 2, 3.5),
            ]);
    }
}
