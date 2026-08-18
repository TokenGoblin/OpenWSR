using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Placefiles;
using OpenWSR.Render;
using Serilog;

namespace OpenWSR.App;

/// <summary>One loaded placefile and its state.</summary>
public sealed class LoadedPlacefile
{
    public required string Source { get; init; }
    public bool Enabled { get; set; } = true;
    public PlacefileDocument? Document { get; set; }
    public DateTime LastFetchUtc { get; set; }
    public string Status { get; set; } = "not loaded";

    public string DisplayName =>
        Document?.Title is { Length: > 0 } title ? title
        : Source.Length > 40 ? "…" + Source[^38..] : Source;
}

/// <summary>
/// Loads and draws GRLevelX placefiles — the community's overlay format. Each file
/// refreshes on the interval it declares, and items obey their own Threshold, so detail
/// appears as you zoom in exactly as the author intended.
/// </summary>
public sealed class PlacefileController : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PlacefileController>();

    private readonly MapView _mapView;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly List<LoadedPlacefile> _files = [];
    private double _lastViewWidthNm;

    public IReadOnlyList<LoadedPlacefile> Files => _files;
    public OverlayGeometry? Geometry { get; private set; }
    public IReadOnlyList<MapView.MapLabel> Labels { get; private set; } = [];

    public event Action? Changed;
    public event Action<string>? StatusChanged;

    public PlacefileController(MapView mapView, string userAgent)
    {
        _mapView = mapView;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    public async Task AddAsync(string source)
    {
        if (_files.Any(f => string.Equals(f.Source, source, StringComparison.OrdinalIgnoreCase)))
        {
            StatusChanged?.Invoke("That placefile is already loaded.");
            return;
        }
        var file = new LoadedPlacefile { Source = source };
        _files.Add(file);
        await LoadAsync(file);
        Rebuild();
        Changed?.Invoke();
    }

    public void Remove(LoadedPlacefile file)
    {
        _files.Remove(file);
        Rebuild();
        Changed?.Invoke();
    }

    public void SetEnabled(LoadedPlacefile file, bool enabled)
    {
        file.Enabled = enabled;
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>Refresh anything past its declared interval, and rebuild when the zoom changes.</summary>
    private async Task TickAsync()
    {
        foreach (var file in _files.Where(f => f.Enabled).ToList())
        {
            var interval = file.Document?.Refresh ?? TimeSpan.FromMinutes(5);
            if (interval < TimeSpan.FromSeconds(15)) interval = TimeSpan.FromSeconds(15);
            if (DateTime.UtcNow - file.LastFetchUtc >= interval)
                await LoadAsync(file);
        }

        double width = ViewWidthNm();
        if (Math.Abs(width - _lastViewWidthNm) / Math.Max(width, 1) > 0.05)
            Rebuild();
    }

    private async Task LoadAsync(LoadedPlacefile file)
    {
        try
        {
            string text;
            if (file.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // GR passes the radar position and version; some generators rely on it.
                var camera = _mapView.Camera.Snapshot();
                var (lat, lon) = GeoMath.FromMercator(camera.CenterX, camera.CenterY);
                var separator = file.Source.Contains('?') ? "&" : "?";
                var url = $"{file.Source}{separator}lat={lat:F3}&lon={lon:F3}&version=1.5";
                text = await _http.GetStringAsync(url);
            }
            else
            {
                text = await File.ReadAllTextAsync(file.Source);
            }

            file.Document = PlacefileParser.Parse(text);
            file.LastFetchUtc = DateTime.UtcNow;
            int count = file.Document.Items.Count;
            file.Status = file.Document.UnsupportedStatements.Count > 0
                ? $"{count} items (skipped {string.Join(", ", file.Document.UnsupportedStatements)})"
                : $"{count} items";
            Rebuild();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            file.Status = $"failed: {ex.Message}";
            file.LastFetchUtc = DateTime.UtcNow; // do not hammer a broken source
            Log.Warning(ex, "Placefile load failed: {Source}", file.Source);
            Changed?.Invoke();
        }
    }

    private double ViewWidthNm()
    {
        var camera = _mapView.Camera.Snapshot();
        return camera.ViewportWidth * camera.MetersPerPixel / 1852.0;
    }

    private void Rebuild()
    {
        double viewWidthNm = ViewWidthNm();
        _lastViewWidthNm = viewWidthNm;
        double metresPerPixel = _mapView.Camera.Snapshot().MetersPerPixel;
        var now = DateTimeOffset.UtcNow;

        var geometry = new OverlayGeometry();
        var labels = new List<MapView.MapLabel>();
        bool any = false;

        foreach (var file in _files.Where(f => f.Enabled && f.Document is not null))
        {
            any = true;
            foreach (var item in file.Document!.Items)
            {
                // The author's own zoom threshold decides when detail appears.
                if (viewWidthNm > item.ThresholdNm) continue;
                if (!item.VisibleAt(now)) continue;

                uint colour = OverlayGeometry.Pack(item.Color.R, item.Color.G, item.Color.B, item.Color.A);
                switch (item)
                {
                    case PlacefileLabel label:
                    {
                        var point = GeoMath.ToMercator(label.LatDeg, label.LonDeg);
                        labels.Add(new MapView.MapLabel(
                            point.X, point.Y, label.Text.Trim(),
                            (int)label.OffsetXPx, (int)label.OffsetYPx));
                        break;
                    }
                    case PlacefileIcon icon:
                    {
                        var point = ToMercatorWithOffset(
                            icon.LatDeg, icon.LonDeg, icon.OffsetXPx, icon.OffsetYPx, metresPerPixel);
                        // Icon sheets are not fetched yet, so each icon shows as a marker.
                        double r = 5 * metresPerPixel;
                        geometry.Lines.Add((point.X - r, point.Y, point.X + r, point.Y, colour, 2f));
                        geometry.Lines.Add((point.X, point.Y - r, point.X, point.Y + r, colour, 2f));
                        break;
                    }
                    case PlacefileLine line:
                    {
                        var points = line.Points
                            .Select(p => ToMercatorWithOffset(
                                item.Anchor?.LatDeg ?? p.LatDeg, item.Anchor?.LonDeg ?? p.LonDeg,
                                item.Anchor is null ? 0 : p.OffsetX,
                                item.Anchor is null ? 0 : p.OffsetY, metresPerPixel))
                            .ToList();
                        for (int i = 1; i < points.Count; i++)
                            geometry.Lines.Add((points[i - 1].X, points[i - 1].Y,
                                points[i].X, points[i].Y, colour, line.WidthPx));
                        break;
                    }
                    case PlacefilePolygon polygon:
                    {
                        foreach (var contour in polygon.Contours)
                        {
                            var points = contour
                                .Select(p => ToMercatorWithOffset(
                                    item.Anchor?.LatDeg ?? p.LatDeg, item.Anchor?.LonDeg ?? p.LonDeg,
                                    item.Anchor is null ? 0 : p.OffsetX,
                                    item.Anchor is null ? 0 : p.OffsetY, metresPerPixel))
                                .ToList();
                            uint fill = OverlayGeometry.Pack(
                                item.Color.R, item.Color.G, item.Color.B, (byte)(item.Color.A / 4));
                            geometry.AddPolygonFill(points, fill);
                            geometry.AddPolygonOutline(points, colour, 1.5f);
                        }
                        break;
                    }
                }
            }
        }

        Geometry = any ? geometry : null;
        Labels = any ? labels : [];
    }

    /// <summary>
    /// Object-block coordinates are screen pixels from the anchor, with +y upward, so
    /// they are converted at the current scale and move with the zoom.
    /// </summary>
    private static (double X, double Y) ToMercatorWithOffset(
        double latDeg, double lonDeg, double offsetXPx, double offsetYPx, double metresPerPixel)
    {
        var point = GeoMath.ToMercator(latDeg, lonDeg);
        return (point.X + offsetXPx * metresPerPixel, point.Y + offsetYPx * metresPerPixel);
    }

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }
}
