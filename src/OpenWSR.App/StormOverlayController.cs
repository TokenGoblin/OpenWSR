using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.Nexrad.Level3;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// Fetches the newest NST/NHI/NMD Level III products for a site and renders them as
/// map geometry: storm tracks (past solid, forecast dotted), hail markers (triangles
/// sized/colored by severity), mesocyclone circles. Refreshes every 2 minutes while
/// enabled.
/// </summary>
public sealed class StormOverlayController : IDisposable
{
    private static readonly uint TrackColor = OverlayGeometry.Pack(240, 240, 240, 220);
    private static readonly uint ForecastColor = OverlayGeometry.Pack(240, 240, 240, 130);
    private static readonly uint HailColor = OverlayGeometry.Pack(80, 230, 120, 230);
    private static readonly uint SevereHailColor = OverlayGeometry.Pack(255, 120, 40, 240);
    private static readonly uint MesoColor = OverlayGeometry.Pack(255, 220, 40, 240);

    private readonly Level3Client _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(2) };
    private string? _site;
    private int _fetchGeneration;

    public OverlayGeometry? Geometry { get; private set; }
    public event Action? GeometryChanged;
    public event Action<string>? StatusChanged;

    public StormOverlayController()
    {
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Enable(string icao)
    {
        _site = icao;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Disable()
    {
        _timer.Stop();
        _site = null;
        _fetchGeneration++;
        Geometry = null;
        GeometryChanged?.Invoke();
    }

    private async Task RefreshAsync()
    {
        if (_site is not { } site) return;
        int generation = ++_fetchGeneration;
        try
        {
            var nst = _client.GetLatestAsync(site, "NST");
            var nhi = _client.GetLatestAsync(site, "NHI");
            var nmd = _client.GetLatestAsync(site, "NMD");
            await Task.WhenAll(nst, nhi, nmd);
            if (generation != _fetchGeneration) return; // toggled off or site changed

            var geometry = new OverlayGeometry();
            int storms = 0, hail = 0, mesos = 0;
            if (nst.Result is { } stormProduct)
                storms = AddStormTracks(geometry, stormProduct);
            if (nhi.Result is { } hailProduct)
                hail = AddHail(geometry, hailProduct);
            if (nmd.Result is { } mesoProduct)
                mesos = AddMesocyclones(geometry, mesoProduct);

            Geometry = geometry;
            GeometryChanged?.Invoke();
            StatusChanged?.Invoke(nst.Result is null && nhi.Result is null && nmd.Result is null
                ? $"No Level III storm products for {site} (quiet weather)."
                : $"Storms: {storms} tracked, {hail} hail, {mesos} meso ({site})");
        }
        catch (Exception ex)
        {
            if (generation == _fetchGeneration)
                StatusChanged?.Invoke($"Level III fetch failed: {ex.Message}");
        }
    }

    private static (double X, double Y) ToMercator(Level3Product p, KmPoint km)
    {
        double range = Math.Sqrt(km.XKm * km.XKm + km.YKm * km.YKm) * 1000.0;
        double azimuth = Math.Atan2(km.XKm, km.YKm); // east over north = bearing
        var (lat, lon) = GeoMath.Offset(p.RadarLatDeg, p.RadarLonDeg, azimuth, range);
        return GeoMath.ToMercator(lat, lon);
    }

    private static int AddStormTracks(OverlayGeometry g, Level3Product p)
    {
        foreach (var cell in p.StormCells)
        {
            var current = ToMercator(p, cell.Position);
            AddDiamond(g, current, 900, TrackColor);

            var previous = current;
            foreach (var past in cell.PastPositions)
            {
                var point = ToMercator(p, past);
                g.Lines.Add((previous.X, previous.Y, point.X, point.Y, TrackColor, 1.5f));
                AddDiamond(g, point, 450, TrackColor);
                previous = point;
            }

            previous = current;
            foreach (var forecast in cell.ForecastPositions)
            {
                var point = ToMercator(p, forecast);
                AddDottedLine(g, previous, point, ForecastColor, 1.5f);
                AddDiamond(g, point, 450, ForecastColor);
                previous = point;
            }
        }
        return p.StormCells.Count;
    }

    private static int AddHail(OverlayGeometry g, Level3Product p)
    {
        int drawn = 0;
        foreach (var h in p.HailIndicators)
        {
            if (h.ProbabilityOfHail <= 0) continue; // unknown (-999) or no hail
            bool severe = h.ProbabilityOfSevereHail >= 30;
            double size = 1200 + 40.0 * Math.Max(0, h.ProbabilityOfSevereHail);
            AddTriangle(g, ToMercator(p, h.Position), size, severe ? SevereHailColor : HailColor);
            drawn++;
        }
        return drawn;
    }

    private static int AddMesocyclones(OverlayGeometry g, Level3Product p)
    {
        foreach (var m in p.Mesocyclones)
        {
            var centre = ToMercator(p, m.Position);
            AddCircle(g, centre, Math.Max(m.RadiusKm, 1.0) * 1000.0, MesoColor, 2f);
            var previous = centre;
            foreach (var past in m.PastPositions)
            {
                var point = ToMercator(p, past);
                g.Lines.Add((previous.X, previous.Y, point.X, point.Y, MesoColor, 1f));
                previous = point;
            }
        }
        return p.Mesocyclones.Count;
    }

    // ---- primitive helpers (sizes in Mercator meters) ----

    private static void AddDiamond(OverlayGeometry g, (double X, double Y) c, double r, uint color)
    {
        g.FillTriangles.Add((c.X - r, c.Y, color));
        g.FillTriangles.Add((c.X, c.Y + r, color));
        g.FillTriangles.Add((c.X + r, c.Y, color));
        g.FillTriangles.Add((c.X - r, c.Y, color));
        g.FillTriangles.Add((c.X + r, c.Y, color));
        g.FillTriangles.Add((c.X, c.Y - r, color));
    }

    private static void AddTriangle(OverlayGeometry g, (double X, double Y) c, double r, uint color)
    {
        // NWS-style hail marker: upward triangle outline.
        var top = (c.X, Y: c.Y + r);
        var left = (X: c.X - 0.87 * r, Y: c.Y - 0.5 * r);
        var right = (X: c.X + 0.87 * r, Y: c.Y - 0.5 * r);
        g.Lines.Add((top.X, top.Y, left.X, left.Y, color, 2f));
        g.Lines.Add((left.X, left.Y, right.X, right.Y, color, 2f));
        g.Lines.Add((right.X, right.Y, top.X, top.Y, color, 2f));
    }

    private static void AddCircle(OverlayGeometry g, (double X, double Y) c, double r, uint color, float width)
    {
        const int segments = 28;
        for (int i = 0; i < segments; i++)
        {
            double a0 = 2 * Math.PI * i / segments;
            double a1 = 2 * Math.PI * (i + 1) / segments;
            g.Lines.Add((
                c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0),
                c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1), color, width));
        }
    }

    private static void AddDottedLine(
        OverlayGeometry g, (double X, double Y) a, (double X, double Y) b, uint color, float width)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        int dashes = Math.Max(1, (int)(length / 2500));
        for (int i = 0; i < dashes; i++)
        {
            double t0 = (double)i / dashes;
            double t1 = t0 + 0.5 / dashes;
            g.Lines.Add((a.X + dx * t0, a.Y + dy * t0, a.X + dx * t1, a.Y + dy * t1, color, width));
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _client.Dispose();
    }
}
