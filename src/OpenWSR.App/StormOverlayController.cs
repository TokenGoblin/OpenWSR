using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.Nexrad.Level3;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// One SCIT cell with its NHI/NMD attributes joined on and motion derived from the
/// forecast track. Path positions are 15 minutes apart (SCIT forecast interval).
/// </summary>
public sealed record TrackedStorm(
    string Id,
    double LatDeg, double LonDeg,
    IReadOnlyList<(double LatDeg, double LonDeg)> ForecastPath, // 15-min steps, nearest first
    IReadOnlyList<(double LatDeg, double LonDeg)> PastPath,
    double SpeedKmh, double BearingDeg,
    int ProbabilityOfHail, int ProbabilityOfSevereHail, int MaxHailSizeInches,
    double? MesoRadiusKm);

/// <summary>
/// Fetches the newest NST/NHI/NMD Level III products for a site, joins them into
/// <see cref="TrackedStorm"/>s, and renders the storm layer subject to the filter
/// flags. Refreshes every 2 minutes while enabled; layers rebuild without refetching.
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
    private Level3Product? _nst, _nhi, _nmd;

    // ---- layer filters (WunderMap-style); Rebuild() applies without refetching ----
    public bool ShowPastTrack { get; set; } = true;
    public bool ShowForecastTrack { get; set; } = true;
    public bool ShowHail { get; set; } = true;
    public bool ShowMeso { get; set; } = true;
    /// <summary>Hide hail markers below this probability-of-severe-hail percentage.</summary>
    public int MinSevereHailProbability { get; set; }

    public OverlayGeometry? Geometry { get; private set; }
    public IReadOnlyList<TrackedStorm> Storms { get; private set; } = [];
    public bool IsEnabled => _site is not null;

    public event Action? GeometryChanged;
    public event Action<IReadOnlyList<TrackedStorm>>? StormsUpdated;
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
        _nst = _nhi = _nmd = null;
        Storms = [];
        Geometry = null;
        GeometryChanged?.Invoke();
        StormsUpdated?.Invoke(Storms);
    }

    /// <summary>Re-render the layer from cached products after a filter change.</summary>
    public void Rebuild()
    {
        if (_site is null) return;
        BuildGeometry();
        GeometryChanged?.Invoke();
    }

    /// <summary>The tracked storm nearest to a point, when within <paramref name="maxKm"/>.</summary>
    public TrackedStorm? HitTest(double latDeg, double lonDeg, double maxKm = 10)
    {
        TrackedStorm? best = null;
        double bestM = maxKm * 1000;
        foreach (var storm in Storms)
        {
            double d = GeoMath.DistanceM(latDeg, lonDeg, storm.LatDeg, storm.LonDeg);
            if (d < bestM)
            {
                bestM = d;
                best = storm;
            }
        }
        return best;
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

            _nst = nst.Result;
            _nhi = nhi.Result;
            _nmd = nmd.Result;
            BuildStorms();
            BuildGeometry();
            GeometryChanged?.Invoke();
            StormsUpdated?.Invoke(Storms);
            StatusChanged?.Invoke(_nst is null && _nhi is null && _nmd is null
                ? $"No Level III storm products for {site} (quiet weather)."
                : $"Storms: {Storms.Count} tracked, " +
                  $"{_nhi?.HailIndicators.Count(h => h.ProbabilityOfHail > 0) ?? 0} hail, " +
                  $"{_nmd?.Mesocyclones.Count ?? 0} meso ({site})");
        }
        catch (Exception ex)
        {
            if (generation == _fetchGeneration)
                StatusChanged?.Invoke($"Level III fetch failed: {ex.Message}");
        }
    }

    // ---- joining + geometry ----

    private static (double LatDeg, double LonDeg) ToLatLon(Level3Product p, KmPoint km)
    {
        double range = Math.Sqrt(km.XKm * km.XKm + km.YKm * km.YKm) * 1000.0;
        double azimuth = Math.Atan2(km.XKm, km.YKm); // east over north = bearing
        return GeoMath.Offset(p.RadarLatDeg, p.RadarLonDeg, azimuth, range);
    }

    private static (double X, double Y) ToMercator(Level3Product p, KmPoint km)
    {
        var (lat, lon) = ToLatLon(p, km);
        return GeoMath.ToMercator(lat, lon);
    }

    private void BuildStorms()
    {
        var storms = new List<TrackedStorm>();
        if (_nst is { } nst)
        {
            foreach (var cell in nst.StormCells)
            {
                var current = ToLatLon(nst, cell.Position);
                var forecast = cell.ForecastPositions.Select(f => ToLatLon(nst, f)).ToList();
                var past = cell.PastPositions.Select(f => ToLatLon(nst, f)).ToList();

                // Motion from the first forecast step (15 min ahead); else the last past step.
                double speed = 0, bearing = 0;
                (double, double)? reference = forecast.Count > 0 ? forecast[0]
                    : past.Count > 0 ? past[0] : null;
                if (reference is { } r)
                {
                    bool toward = forecast.Count > 0;
                    double meters = GeoMath.DistanceM(current.LatDeg, current.LonDeg, r.Item1, r.Item2);
                    speed = meters / 1000.0 * 4; // 15 min -> per hour
                    bearing = GeoMath.BearingRad(current.LatDeg, current.LonDeg, r.Item1, r.Item2)
                              * 180.0 / Math.PI;
                    if (!toward) bearing += 180.0; // past point: motion is away from it
                    bearing = (bearing + 360.0) % 360.0;
                }

                var hail = FindHail(cell);
                var meso = FindMeso(nst, cell);
                storms.Add(new TrackedStorm(
                    cell.Id, current.LatDeg, current.LonDeg, forecast, past, speed, bearing,
                    hail?.ProbabilityOfHail ?? 0, hail?.ProbabilityOfSevereHail ?? 0,
                    hail?.MaxHailSizeInches ?? 0, meso));
            }
        }
        Storms = storms;
    }

    private HailIndicator? FindHail(StormCell cell)
    {
        if (_nhi is not { } nhi) return null;
        var byId = nhi.HailIndicators.FirstOrDefault(h => h.StormId == cell.Id);
        if (byId is not null) return byId;
        return nhi.HailIndicators.FirstOrDefault(h =>
            Math.Abs(h.Position.XKm - cell.Position.XKm) < 0.5 &&
            Math.Abs(h.Position.YKm - cell.Position.YKm) < 0.5);
    }

    private double? FindMeso(Level3Product nst, StormCell cell)
    {
        if (_nmd is not { } nmd) return null;
        foreach (var m in nmd.Mesocyclones)
        {
            double dx = m.Position.XKm - cell.Position.XKm;
            double dy = m.Position.YKm - cell.Position.YKm;
            if (dx * dx + dy * dy < 8 * 8)
                return m.RadiusKm;
        }
        return null;
    }

    private void BuildGeometry()
    {
        var geometry = new OverlayGeometry();
        if (_nst is { } nst)
        {
            foreach (var cell in nst.StormCells)
            {
                var current = ToMercator(nst, cell.Position);
                AddDiamond(geometry, current, 900, TrackColor);

                if (ShowPastTrack)
                {
                    var previous = current;
                    foreach (var past in cell.PastPositions)
                    {
                        var point = ToMercator(nst, past);
                        geometry.Lines.Add((previous.X, previous.Y, point.X, point.Y, TrackColor, 1.5f));
                        AddDiamond(geometry, point, 450, TrackColor);
                        previous = point;
                    }
                }
                if (ShowForecastTrack)
                {
                    var previous = current;
                    for (int i = 0; i < cell.ForecastPositions.Count; i++)
                    {
                        var point = ToMercator(nst, cell.ForecastPositions[i]);
                        AddDottedLine(geometry, previous, point, ForecastColor, 1.5f);
                        // Time markers shrink with lead time: +15/+30/+45/+60 min.
                        AddDiamond(geometry, point, 700 - i * 120, ForecastColor);
                        previous = point;
                    }
                    if (cell.ForecastPositions.Count > 0)
                        AddArrowHead(geometry, cell, nst);
                }
            }
        }
        if (ShowHail && _nhi is { } nhi)
        {
            foreach (var h in nhi.HailIndicators)
            {
                if (h.ProbabilityOfHail <= 0) continue;
                if (h.ProbabilityOfSevereHail < MinSevereHailProbability) continue;
                bool severe = h.ProbabilityOfSevereHail >= 30;
                double size = 1200 + 40.0 * Math.Max(0, h.ProbabilityOfSevereHail);
                AddTriangle(geometry, ToMercator(nhi, h.Position), size,
                    severe ? SevereHailColor : HailColor);
            }
        }
        if (ShowMeso && _nmd is { } nmd)
        {
            foreach (var m in nmd.Mesocyclones)
            {
                var centre = ToMercator(nmd, m.Position);
                AddCircle(geometry, centre, Math.Max(m.RadiusKm, 1.0) * 1000.0, MesoColor, 2f);
                var previous = centre;
                foreach (var past in m.PastPositions)
                {
                    var point = ToMercator(nmd, past);
                    geometry.Lines.Add((previous.X, previous.Y, point.X, point.Y, MesoColor, 1f));
                    previous = point;
                }
            }
        }
        Geometry = geometry;
    }

    private static void AddArrowHead(OverlayGeometry g, StormCell cell, Level3Product p)
    {
        var last = cell.ForecastPositions[^1];
        var from = cell.ForecastPositions.Count > 1
            ? cell.ForecastPositions[^2]
            : cell.Position;
        var tip = ToMercator(p, last);
        var back = ToMercator(p, from);
        double dx = tip.X - back.X, dy = tip.Y - back.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        dx /= length;
        dy /= length;
        const double arm = 1800;
        g.Lines.Add((tip.X, tip.Y, tip.X - arm * (dx * 0.87 - dy * 0.5), tip.Y - arm * (dy * 0.87 + dx * 0.5), ForecastColor, 2f));
        g.Lines.Add((tip.X, tip.Y, tip.X - arm * (dx * 0.87 + dy * 0.5), tip.Y - arm * (dy * 0.87 - dx * 0.5), ForecastColor, 2f));
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
        var top = (c.X, Y: c.Y + r);
        var left = (X: c.X - 0.87 * r, Y: c.Y - 0.5 * r);
        var right = (X: c.X + 0.87 * r, Y: c.Y - 0.5 * r);
        g.Lines.Add((top.X, top.Y, left.X, left.Y, color, 2f));
        g.Lines.Add((left.X, left.Y, right.X, right.Y, color, 2f));
        g.Lines.Add((right.X, right.Y, top.X, top.Y, color, 2f));
    }

    internal static void AddCircle(OverlayGeometry g, (double X, double Y) c, double r, uint color, float width)
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
