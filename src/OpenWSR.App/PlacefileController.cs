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
public sealed class PlacefileController : IDisposable, ITimedLayer
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PlacefileController>();

    private readonly MapView _mapView;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly List<LoadedPlacefile> _files = [];
    private double _lastViewWidthNm;

    /// <summary>
    /// Decoded icon sheets, keyed by resolved URL so two placefiles naming the same sheet
    /// fetch it once. Decoding happens here rather than in the renderer because imaging is
    /// WPF's job and OpenWSR.Render has no PNG decoder.
    /// </summary>
    private readonly Dictionary<string, MapView.IconSheet> _sheetCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sheets handed to the renderer this rebuild, numbered as the layer sees them.</summary>
    private readonly Dictionary<int, MapView.IconSheet> _activeSheets = [];

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

    private bool _pollingSuspended;

    /// <summary>See <see cref="ITimedLayer"/>: stop the clock, keep the loaded files.</summary>
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
        _timer.Start();
        _ = TickAsync();   // a placefile refreshes on its own schedule; it is due by now
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
            await LoadIconSheetsAsync(file);
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

    /// <summary>
    /// Fetch and decode every icon sheet a file declares.
    ///
    /// A sheet name is usually relative to the placefile's own URL, which is why the
    /// original source is needed to resolve it. A sheet that fails to load leaves its icons
    /// drawn as plain markers rather than failing the whole file — one missing PNG should
    /// not cost you the overlay.
    /// </summary>
    private async Task LoadIconSheetsAsync(LoadedPlacefile file)
    {
        if (file.Document is not { IconSheets.Count: > 0 } document) return;

        foreach (var sheet in document.IconSheets)
        {
            string url = ResolveSheetUrl(file.Source, sheet.Source);
            if (_sheetCache.ContainsKey(url)) continue;

            try
            {
                byte[] bytes = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? await _http.GetByteArrayAsync(url)
                    : await File.ReadAllBytesAsync(url);

                var decoded = DecodeSheet(bytes, sheet.WidthPx, sheet.HeightPx);
                if (decoded is not null) _sheetCache[url] = decoded;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Icon sheet failed: {Url}", url);
            }
        }
    }

    /// <summary>
    /// Sheet names are usually relative to the placefile. Absolute URLs and local paths are
    /// taken as given.
    /// </summary>
    internal static string ResolveSheetUrl(string placefileSource, string sheetName)
    {
        if (sheetName.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return sheetName;

        if (placefileSource.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(new Uri(placefileSource), sheetName, out var absolute))
            return absolute.ToString();

        var directory = Path.GetDirectoryName(placefileSource);
        return string.IsNullOrEmpty(directory) ? sheetName : Path.Combine(directory, sheetName);
    }

    /// <summary>Decode to straight BGRA. WPF handles PNG and GIF, which is what these are.</summary>
    internal static MapView.IconSheet? DecodeSheet(byte[] bytes, int cellWidth, int cellHeight)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;

        var frame = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if (width <= 0 || height <= 0) return null;

        var pixels = new byte[width * height * 4];
        frame.CopyPixels(pixels, width * 4, 0);

        // Sheets in the wild frequently have no alpha channel at all — the IEM wind-barb
        // sheet is white-on-black RGB — and the GRLevelX convention is that black is the
        // transparent colour when there is nothing else to go on. Without this the icons
        // draw as solid black tiles with the artwork buried inside them.
        bool opaqueThroughout = true;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 255) continue;
            opaqueThroughout = false;
            break;
        }
        if (opaqueThroughout)
        {
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0)
                    pixels[i + 3] = 0;
        }

        return new MapView.IconSheet(pixels, width, height, cellWidth, cellHeight);
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
        var icons = new List<MapView.MapIcon>();
        _activeSheets.Clear();
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

                        var declared = file.Document!.IconSheets
                            .FirstOrDefault(sheet => sheet.Number == icon.FileNumber);
                        MapView.IconSheet? loaded = declared is null
                            ? null
                            : _sheetCache.GetValueOrDefault(ResolveSheetUrl(file.Source, declared.Source));

                        if (declared is not null && loaded is not null)
                        {
                            // Sheets are renumbered per rebuild: a placefile's own numbering
                            // is local to that file, and several files may all call theirs 1.
                            int id = _activeSheets.FirstOrDefault(kv => ReferenceEquals(kv.Value, loaded)).Key;
                            if (id == 0 || !_activeSheets.ContainsKey(id))
                            {
                                id = _activeSheets.Count + 1;
                                _activeSheets[id] = loaded;
                            }
                            icons.Add(new MapView.MapIcon(
                                point.X, point.Y, id, icon.IconNumber, icon.AngleDeg,
                                declared.HotXPx / (double)declared.WidthPx,
                                declared.HotYPx / (double)declared.HeightPx));
                        }
                        else
                        {
                            // No sheet: a plain marker still says something is here.
                            double r = 5 * metresPerPixel;
                            geometry.Lines.Add(new OverlayLine(point.X - r, point.Y, point.X + r, point.Y, colour, 2f, LineCaps.Both));
                            geometry.Lines.Add(new OverlayLine(point.X, point.Y - r, point.X, point.Y + r, colour, 2f, LineCaps.Both));
                        }
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

        // Icons go straight to the renderer rather than through the composed overlay: they
        // are textured and rotatable, which the triangle-and-line geometry cannot carry.
        _mapView.SetIcons(
            new Dictionary<int, MapView.IconSheet>(_activeSheets),
            any ? icons : []);
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
