using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// National convective context: SPC outlooks, mesoscale discussions, watch boxes and
/// local storm reports. Refreshes every 5 minutes; layers rebuild from cache when a
/// filter changes.
/// </summary>
public sealed class OutlookOverlayController : IDisposable, ITimedLayer
{
    private readonly OutlookClient _client;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private IReadOnlyList<OutlookArea> _areas = [];
    private IReadOnlyList<StormReport> _reports = [];
    private bool _started;

    public bool ShowOutlooks { get; set; }
    public bool ShowDiscussions { get; set; }
    public bool ShowWatches { get; set; }
    public bool ShowReports { get; set; }

    public OverlayGeometry? Geometry { get; private set; }
    public IReadOnlyList<MapView.MapLabel> Labels { get; private set; } = [];

    public event Action? GeometryChanged;
    public event Action<string>? StatusChanged;

    public OutlookOverlayController(string userAgent)
    {
        _client = new OutlookClient(userAgent);
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>True when at least one of these layers is switched on.</summary>
    public bool AnyEnabled => ShowOutlooks || ShowDiscussions || ShowWatches || ShowReports;

    public async Task ApplyAsync()
    {
        if (!AnyEnabled)
        {
            _timer.Stop();
            _started = false;
            Geometry = null;
            Labels = [];
            GeometryChanged?.Invoke();
            return;
        }
        if (!_started)
        {
            _started = true;
            if (_pollingSuspended) return;   // hidden: this starts when the window comes back
            _timer.Start();
            await RefreshAsync();
            return;
        }
        Rebuild();
    }

    private bool _pollingSuspended;

    /// <summary>
    /// See <see cref="ITimedLayer"/>. A latch, not a snapshot: it stays set until the window
    /// comes back, so a layer switched *on* while hidden — which is what restoring the saved
    /// toggles does on a tray start — takes its state without starting to fetch.
    /// </summary>
    public void SuspendPolling()
    {
        _pollingSuspended = true;
        _timer.Stop();
    }

    /// <inheritdoc />
    public void ResumePolling()
    {
        if (!_pollingSuspended) return;
        _pollingSuspended = false;
        if (!_started) return;
        _timer.Start();
        _ = RefreshAsync();   // what it holds is at least as old as the pause
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (ShowOutlooks || ShowDiscussions || ShowWatches)
                _areas = await _client.GetConvectiveAsync();
            if (ShowReports)
                _reports = await _client.GetStormReportsAsync();
            Rebuild();

            int outlooks = _areas.Count(a => a.Kind == "outlook");
            int mcds = _areas.Count(a => a.Kind == "mcd");
            int watches = _areas.Count(a => a.Kind == "watch");
            StatusChanged?.Invoke(
                $"National: {outlooks} outlook area(s), {mcds} discussion(s), " +
                $"{watches} watch box(es), {_reports.Count} storm report(s)");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"National products fetch failed: {ex.Message}");
        }
    }

    /// <summary>SPC categorical risk colours, following their published scheme.</summary>
    private static (byte R, byte G, byte B) RiskColour(int level) => level switch
    {
        2 => (0x7B, 0xC6, 0x7B), // marginal, green
        3 => (0xF6, 0xE9, 0x78), // slight, yellow
        4 => (0xE5, 0xBC, 0x6E), // enhanced, orange
        5 => (0xE8, 0x7C, 0x7C), // moderate, red
        _ => (0xE8, 0x7C, 0xE8), // high, magenta
    };

    private void Rebuild()
    {
        var geometry = new OverlayGeometry();
        var labels = new List<MapView.MapLabel>();

        foreach (var area in _areas)
        {
            bool show = area.Kind switch
            {
                "outlook" => ShowOutlooks,
                "mcd" => ShowDiscussions,
                "watch" => ShowWatches,
                _ => false,
            };
            if (!show) continue;
            if (area.Expires is { } expires && expires < DateTimeOffset.UtcNow) continue;

            var (r, g, b) = area.Kind switch
            {
                "outlook" => RiskColour(area.Level),
                "mcd" => ((byte)0xC8, (byte)0xC8, (byte)0xF0),
                _ => area.Level >= 6 ? ((byte)0xFF, (byte)0x64, (byte)0x64)
                                     : ((byte)0xFF, (byte)0xD2, (byte)0x50),
            };
            // Outlooks fill lightly; watches and discussions are outline-only so they
            // never obscure the radar underneath.
            uint fill = OverlayGeometry.Pack(r, g, b, area.Kind == "outlook" ? (byte)30 : (byte)12);
            uint stroke = OverlayGeometry.Pack(r, g, b, 225);
            float width = area.Kind == "outlook" ? 1.5f : 2.5f;

            foreach (var ring in area.Rings)
            {
                var mercator = ring
                    .Select(p => GeoMath.ToMercator(p.LatDeg, p.LonDeg))
                    .Select(m => (m.X, m.Y))
                    .ToList();
                geometry.AddPolygonFill(mercator, fill);
                geometry.AddPolygonOutline(mercator, stroke, width);
            }

            if (area.Kind != "outlook" && area.Rings.Count > 0)
            {
                var first = area.Rings[0];
                double lat = first.Average(p => p.LatDeg);
                double lon = first.Average(p => p.LonDeg);
                var centre = GeoMath.ToMercator(lat, lon);
                labels.Add(new MapView.MapLabel(centre.X, centre.Y, area.Category.ToUpperInvariant(), -30, 0));
            }
        }

        if (ShowReports)
        {
            foreach (var report in _reports)
            {
                var point = GeoMath.ToMercator(report.LatDeg, report.LonDeg);
                var (r, g, b) = report.Type.Contains("TORNADO", StringComparison.OrdinalIgnoreCase)
                    ? ((byte)0xFF, (byte)0x3C, (byte)0x3C)
                    : report.Type.Contains("HAIL", StringComparison.OrdinalIgnoreCase)
                        ? ((byte)0x4C, (byte)0xE0, (byte)0xFF)
                        : ((byte)0xFF, (byte)0xC8, (byte)0x50);
                uint colour = OverlayGeometry.Pack(r, g, b, 240);
                AddCross(geometry, (point.X, point.Y), 2200, colour);
                if (report.Magnitude.Length > 0)
                    labels.Add(new MapView.MapLabel(point.X, point.Y, report.Magnitude, 9, 3));
            }
        }

        Geometry = geometry;
        Labels = labels;
        GeometryChanged?.Invoke();
    }

    /// <summary>Storm reports read as an X so they stay distinct from storm-cell diamonds.</summary>
    private static void AddCross(OverlayGeometry g, (double X, double Y) c, double r, uint colour)
    {
        g.Lines.Add(new OverlayLine(c.X - r, c.Y - r, c.X + r, c.Y + r, colour, 2f, LineCaps.Both));
        g.Lines.Add(new OverlayLine(c.X - r, c.Y + r, c.X + r, c.Y - r, colour, 2f, LineCaps.Both));
    }

    public void Dispose()
    {
        _timer.Stop();
        _client.Dispose();
    }
}
