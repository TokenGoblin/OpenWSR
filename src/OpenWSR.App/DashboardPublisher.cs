using OpenWSR.Dashboard;
using OpenWSR.Geo;
using OpenWSR.Ingest;

namespace OpenWSR.App;

/// <summary>
/// Owns the LAN dashboard server and keeps what it serves in step with what the app is
/// watching.
///
/// Everything here runs on the UI thread, because that is where the controllers' state lives;
/// what crosses to the server's threads is one immutable <see cref="DashboardSnapshot"/> at a
/// time. The server sits on the watching side of the tray line: it adds no clock and fetches
/// nothing, so it costs nothing while hidden beyond answering the requests that arrive.
/// </summary>
public sealed class DashboardPublisher : IAsyncDisposable
{
    /// <summary>
    /// How far from a watched place a warning still goes on the page. The warnings poll is
    /// national, and several hundred polygons from the far side of the country would be most
    /// of every payload while telling a home dashboard nothing.
    /// </summary>
    internal const double AlertReachKm = 500;

    private readonly DashboardServer _server = new();
    private readonly SemaphoreSlim _applying = new(1, 1);
    private int? _runningPort;

    public bool IsRunning => _server.IsRunning;

    /// <summary>The port being served, or null when off.</summary>
    public int? Port => _server.Port;

    /// <summary>Raised on the UI thread whenever the server starts or stops.</summary>
    public event Action? RunningChanged;

    /// <summary>
    /// Bring the server to what settings ask for: start, stop, or move to a new port. Throws
    /// <see cref="IOException"/> with a message fit for the error bar when it cannot listen.
    /// </summary>
    public async Task ApplyAsync(bool enabled, int port)
    {
        // One at a time. Startup and a Settings save can both ask, and the server only counts
        // as running once its bind has finished — so an overlapping second call would see it
        // stopped and start another, which either fails with a false "port in use" or, on a
        // different port, leaks a listener nothing can stop. Queued calls carry the newer
        // settings, so the last one in is the one that holds.
        await _applying.WaitAsync();
        try
        {
            if (enabled && _runningPort == port && _server.IsRunning) return;

            if (_server.IsRunning)
            {
                await _server.StopAsync();
                _runningPort = null;
                RunningChanged?.Invoke();
            }
            if (!enabled) return;

            await _server.StartAsync(port);
            _runningPort = port;
            RunningChanged?.Invoke();
        }
        finally
        {
            _applying.Release();
        }
    }

    /// <summary>Publish the current state of the watch. Cheap enough to call on every change.</summary>
    public void Publish(
        ThreatMonitor threats, WarningsController warnings, StormOverlayController storms)
    {
        // Nobody is listening, so the snapshot would only be built to be thrown away.
        if (!_server.IsRunning) return;
        _server.Publish(Build(
            DateTimeOffset.UtcNow,
            threats.Places,
            threats.Current,
            warnings.AllAlerts,
            warnings.LastRefreshUtc,
            storms.Site,
            storms.Storms,
            storms.LastRefreshUtc));
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    /// <summary>
    /// The snapshot, from plain values. Kept apart from the controllers so it can be tested
    /// without a map or a network.
    /// </summary>
    internal static DashboardSnapshot Build(
        DateTimeOffset now,
        IReadOnlyList<WatchedPlace> places,
        IReadOnlyList<Threat> threats,
        IReadOnlyList<ActiveAlert> alerts,
        DateTimeOffset? alertsUpdated,
        string? stormSite,
        IReadOnlyList<TrackedStorm> storms,
        DateTimeOffset? stormsUpdated)
    {
        return new DashboardSnapshot(
            UpdatedUtc: now,
            AlertsUpdatedUtc: alertsUpdated,
            StormsUpdatedUtc: stormSite is null ? null : stormsUpdated,
            StormSite: stormSite,
            // The monitor lists the primary first; see ThreatMonitor.Configure.
            Places: [.. places.Select((p, i) => new DashboardPlace(p.Name, p.LatDeg, p.LonDeg, p.RadiusKm, i == 0))],
            Threats: [.. threats.Select(ToDashboard)],
            Alerts: [.. alerts.Where(a => IsNear(a, places)).Select(ToDashboard)],
            Storms: [.. storms.Select(ToDashboard)]);
    }

    private static DashboardThreat ToDashboard(Threat t) => new(
        t.Key, t.Title, t.Label, t.Detail, t.Range,
        t.Rank.ToString().ToLowerInvariant(), t.Interrupts,
        t.DistanceKm, t.EtaMinutes, t.LatDeg, t.LonDeg, t.Expires, t.PlaceName);

    private static DashboardAlert ToDashboard(ActiveAlert a)
    {
        var (r, g, b) = WarningsController.ColorFor(a.Event);
        return new DashboardAlert(
            a.Id, a.Event, a.Headline, a.Severity, a.Expires, $"#{r:x2}{g:x2}{b:x2}",
            [.. a.Polygons.Select(ring => ring.Select(p => new[] { p.LatDeg, p.LonDeg }).ToArray())]);
    }

    private static DashboardStorm ToDashboard(TrackedStorm s) => new(
        s.Id, s.LatDeg, s.LonDeg,
        [.. s.ForecastPath.Select(p => new[] { p.LatDeg, p.LonDeg })],
        [.. s.PastPath.Select(p => new[] { p.LatDeg, p.LonDeg })],
        s.SpeedKmh is { } kmh && s.BearingDeg is { } deg
            ? $"{ThreatMonitor.CompassPoint(deg)} {Units.Speed(kmh)}"
            : null,
        s.ProbabilityOfHail, s.ProbabilityOfSevereHail, s.MaxHailSizeInches, s.MesoRadiusKm);

    /// <summary>
    /// Whether any vertex of the warning lies within reach of any watched place. A vertex
    /// test is enough at this scale — a county-sized polygon wholly around a place has
    /// vertices well inside 500 km of it. With nothing watched there is no "near", so the
    /// whole country goes out rather than nothing.
    /// </summary>
    internal static bool IsNear(ActiveAlert alert, IReadOnlyList<WatchedPlace> places)
    {
        if (places.Count == 0) return true;
        foreach (var ring in alert.Polygons)
            foreach (var (lat, lon) in ring)
                foreach (var place in places)
                    if (GeoMath.DistanceM(place.LatDeg, place.LonDeg, lat, lon) <= AlertReachKm * 1000)
                        return true;
        return false;
    }
}
