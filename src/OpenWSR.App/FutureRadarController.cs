using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Grib2;
using OpenWSR.Ingest;
using OpenWSR.Palettes;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// Future radar: HRRR simulated composite reflectivity, resampled from its Lambert grid
/// into a Web Mercator raster once per frame at load time, then cycled like a loop.
/// Answers "will it rain on me in two hours", which the archive and live feeds cannot.
/// </summary>
public sealed class FutureRadarController : IDisposable
{
    /// <summary>Resolution of the resampled CONUS raster. 2048 is ~2.5 km per pixel.</summary>
    private const int RasterSize = 2048;

    private readonly MapView _mapView;
    private readonly HrrrClient _client;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private List<(DateTime Valid, MapView.ImageOverlay Image)> _frames = [];
    private int _position;
    private CancellationTokenSource? _cts;

    public bool IsLoaded => _frames.Count > 0;
    public bool IsPlaying { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<int, int, DateTime>? FrameChanged; // index, count, valid time

    public FutureRadarController(MapView mapView, string userAgent)
    {
        _mapView = mapView;
        _client = new HrrrClient(userAgent);
        _timer.Tick += (_, _) => Advance();
    }

    public async Task LoadAsync(int hours = 6)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Clear();
        StatusChanged?.Invoke("Fetching HRRR forecast…");

        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
                StatusChanged?.Invoke($"Fetching HRRR forecast… hour {p.Done} of {p.Total}"));
            var forecast = await Task.Run(() => _client.GetForecastAsync(hours, progress, cts.Token), cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            if (forecast.Count == 0)
            {
                StatusChanged?.Invoke("No HRRR forecast available right now.");
                return;
            }

            var table = BuiltinTables.Reflectivity;
            var palette = table.BuildRgba256();
            var frames = await Task.Run(() => forecast
                .Select(f => (f.ValidTimeUtc, Rasterize(f.Field, palette, table.MinValue, table.Range)))
                .ToList(), cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            _frames = frames;
            _position = 0;
            Show(0);
            StatusChanged?.Invoke(
                $"Future radar: {_frames.Count} hours from the {forecast[0].ValidTimeUtc.AddHours(-forecast[0].ForecastHour):HH}z HRRR run");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"HRRR fetch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resample the model's Lambert grid into Mercator. Walking the output raster and
    /// projecting each pixel back into grid space avoids holes, which the forward
    /// direction would leave wherever the grid stretches.
    /// </summary>
    private static MapView.ImageOverlay Rasterize(
        Grib2Field field, byte[] palette, float paletteMin, float paletteRange)
    {
        var grid = field.Grid;
        var projection = new LambertConformal(
            grid.Latin1Deg, grid.Latin2Deg, grid.LovDeg, grid.LadDeg);
        var (originX, originY) = projection.Forward(grid.Lat1Deg, grid.Lon1Deg);

        // Mercator bounds of the grid's four corners, padded a little.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (i, j) in new[] { (0, 0), (grid.Nx - 1, 0), (0, grid.Ny - 1), (grid.Nx - 1, grid.Ny - 1) })
        {
            var (lat, lon) = projection.Inverse(
                originX + i * grid.DxMetres, originY + j * grid.DyMetres);
            var (mx, my) = GeoMath.ToMercator(lat, lon);
            minX = Math.Min(minX, mx); maxX = Math.Max(maxX, mx);
            minY = Math.Min(minY, my); maxY = Math.Max(maxY, my);
        }

        var bgra = new byte[RasterSize * RasterSize * 4];
        double spanX = maxX - minX, spanY = maxY - minY;
        float invRange = 1f / paletteRange;

        Parallel.For(0, RasterSize, row =>
        {
            // Row 0 is the top of the image, which is maximum Mercator y.
            double my = maxY - (row + 0.5) / RasterSize * spanY;
            for (int column = 0; column < RasterSize; column++)
            {
                double mx = minX + (column + 0.5) / RasterSize * spanX;
                var (lat, lon) = GeoMath.FromMercator(mx, my);
                var (x, y) = projection.Forward(lat, lon);

                int i = (int)Math.Round((x - originX) / grid.DxMetres);
                int j = (int)Math.Round((y - originY) / grid.DyMetres);
                if (i < 0 || i >= grid.Nx || j < 0 || j >= grid.Ny) continue;

                float value = field.Values[j * grid.Nx + i];
                if (float.IsNaN(value) || value < 5f) continue; // below-threshold stays clear

                int level = (int)Math.Clamp((value - paletteMin) * invRange * 255f, 0, 255);
                int source = level * 4;
                int target = (row * RasterSize + column) * 4;
                // Palette is RGBA; the texture wants BGRA.
                bgra[target + 0] = palette[source + 2];
                bgra[target + 1] = palette[source + 1];
                bgra[target + 2] = palette[source + 0];
                bgra[target + 3] = palette[source + 3];
            }
        });

        return new MapView.ImageOverlay(bgra, RasterSize, RasterSize, minX, minY, maxX, maxY, 0.85f);
    }

    private void Show(int index)
    {
        if (index < 0 || index >= _frames.Count) return;
        _position = index;
        var (valid, image) = _frames[index];
        _mapView.SetImageOverlay(image);
        FrameChanged?.Invoke(index, _frames.Count, valid);
    }

    public void Step(int delta)
    {
        if (_frames.Count == 0) return;
        Show((_position + delta + _frames.Count) % _frames.Count);
    }

    public void Play()
    {
        if (_frames.Count == 0) return;
        IsPlaying = true;
        _timer.Start();
    }

    public void Pause()
    {
        IsPlaying = false;
        _timer.Stop();
    }

    private void Advance() => Step(1);

    public void Clear()
    {
        Pause();
        _frames = [];
        _position = 0;
        _mapView.SetImageOverlay(null);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _timer.Stop();
        _client.Dispose();
    }
}
