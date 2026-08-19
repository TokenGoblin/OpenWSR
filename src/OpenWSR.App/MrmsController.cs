using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Grib2;
using OpenWSR.Ingest;
using OpenWSR.Palettes;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// The national mosaic from MRMS itself rather than someone else's pre-rendered tiles.
///
/// The tile mosaic is published only to zoom 12 and is stretched above it, so the seamless
/// national picture turns to mush exactly when you lean in on a storm. The MRMS grid is
/// 0.01° — about a kilometre — so drawing it directly stays sharp all the way down, uses
/// the same colour table as the radar display, and does not depend on a third party
/// continuing to render tiles.
/// </summary>
public sealed class MrmsController : IDisposable
{
    /// <summary>Output raster edge. Oversampling a 1400-pixel viewport keeps it crisp while panning.</summary>
    private const int RasterSize = 2048;

    /// <summary>
    /// The rasterised area is this much wider than the viewport, so small pans reuse it
    /// instead of triggering a rebuild on every frame.
    /// </summary>
    private const double Overscan = 1.35;

    /// <summary>
    /// Below this, nothing is drawn. MRMS reports down into the noise and painting all of
    /// it puts a grey haze over the whole country; 5 dBZ is where the pre-rendered mosaic
    /// starts too, so the two look like the same product.
    /// </summary>
    private const float MinDbz = 5f;

    /// <summary>MRMS flags "no radar coverage here" with a large negative rather than a NaN.</summary>
    private const float NoCoverage = -900f;

    private readonly MapView _mapView;
    private readonly MrmsClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(2) };

    // The decoded grid, kept as palette levels rather than floats: 24 MB instead of 98 MB
    // for the same picture, since it is only ever drawn through a 256-entry table.
    private byte[]? _levels;
    private Grib2Grid? _grid;
    private DateTime _validUtc;
    private bool _busy;

    private double _rasterMinX, _rasterMinY, _rasterMaxX, _rasterMaxY;
    private double _rasterMetresPerPixel;
    private float _opacity = 0.75f;

    public bool IsEnabled { get; private set; }
    public DateTime ValidUtc => _validUtc;

    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorRaised;

    public MrmsController(MapView mapView)
    {
        _mapView = mapView;
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public float Opacity
    {
        get => _opacity;
        set
        {
            _opacity = Math.Clamp(value, 0f, 1f);
            if (_levels is not null) Rasterise();
        }
    }

    public void Enable()
    {
        IsEnabled = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Disable()
    {
        IsEnabled = false;
        _timer.Stop();
        _levels = null;
        _grid = null;
        _mapView.SetImageOverlay(MapView.OverlaySlot.Field, null);
    }

    /// <summary>
    /// Re-cut the raster when the view has left the area last drawn. Called from the app's
    /// status tick, so it costs nothing while the map is still.
    /// </summary>
    public void NotifyViewChanged()
    {
        if (!IsEnabled || _levels is null) return;

        var camera = _mapView.Camera.Snapshot();
        var (minX, minY, maxX, maxY) = camera.WorldBounds();

        bool scaleMoved =
            Math.Abs(camera.MetersPerPixel - _rasterMetresPerPixel) / _rasterMetresPerPixel > 0.05;
        bool panned = minX < _rasterMinX || maxX > _rasterMaxX ||
                      minY < _rasterMinY || maxY > _rasterMaxY;

        if (scaleMoved || panned) Rasterise();
    }

    private async Task RefreshAsync()
    {
        if (!IsEnabled || _busy) return;
        _busy = true;
        try
        {
            StatusChanged?.Invoke("MRMS: fetching the national composite…");
            var product = await _client.GetLatestAsync();
            if (product is null)
            {
                StatusChanged?.Invoke("MRMS: nothing published in the last two days.");
                return;
            }

            // Decoding 24.5 million points is not a UI-thread job.
            var (levels, grid) = await Task.Run(() => Quantise(product.Grib2));
            if (!IsEnabled) return;

            _levels = levels;
            _grid = grid;
            _validUtc = product.TimeUtc;
            Rasterise();

            double age = (DateTime.UtcNow - product.TimeUtc).TotalMinutes;
            StatusChanged?.Invoke(
                $"MRMS composite {product.TimeUtc:HH:mm}Z ({age:F0} min old), {grid.Nx}×{grid.Ny} at {grid.DxDeg:F2}°");
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke($"Could not load the MRMS composite: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Decode and reduce to palette levels in one pass. Level 0 is "draw nothing", which
    /// covers both the no-coverage flag and anything under the display threshold.
    /// </summary>
    public static (byte[] Levels, Grib2Grid Grid) Quantise(byte[] grib2)
    {
        var field = Grib2File.Decode(grib2);
        var table = BuiltinTables.Reflectivity;
        float min = table.MinValue, range = table.Range;

        var levels = new byte[field.Values.Length];
        Parallel.For(0, field.Values.Length, i =>
        {
            float value = field.Values[i];
            if (value <= NoCoverage || float.IsNaN(value) || value < MinDbz) return;
            levels[i] = (byte)Math.Clamp((value - min) / range * 255f, 1, 255);
        });
        return (levels, field.Grid);
    }

    /// <summary>
    /// Paint the part of the grid the camera can see.
    ///
    /// Rasterising the visible window rather than the whole country is what keeps this
    /// sharp: a single CONUS-wide texture would be coarser than the pre-rendered tiles it
    /// is meant to improve on. Each output pixel is projected back into grid space, which
    /// avoids the holes a forward mapping leaves.
    /// </summary>
    private void Rasterise()
    {
        if (_levels is not { } levels || _grid is not { } grid) return;

        var camera = _mapView.Camera.Snapshot();
        var (viewMinX, viewMinY, viewMaxX, viewMaxY) = camera.WorldBounds();

        double centreX = (viewMinX + viewMaxX) / 2, centreY = (viewMinY + viewMaxY) / 2;
        double halfX = (viewMaxX - viewMinX) / 2 * Overscan;
        double halfY = (viewMaxY - viewMinY) / 2 * Overscan;

        // Keep the raster square in Mercator so the projection back is uniform.
        double half = Math.Max(halfX, halfY);
        double minX = centreX - half, maxX = centreX + half;
        double minY = centreY - half, maxY = centreY + half;

        var bgra = RenderRegion(
            levels, grid, minX, minY, maxX, maxY, RasterSize,
            BuiltinTables.Reflectivity.BuildRgba256());

        _rasterMinX = minX; _rasterMaxX = maxX;
        _rasterMinY = minY; _rasterMaxY = maxY;
        _rasterMetresPerPixel = camera.MetersPerPixel;

        _mapView.SetImageOverlay(MapView.OverlaySlot.Field, new MapView.ImageOverlay(
            bgra, RasterSize, RasterSize, minX, minY, maxX, maxY, _opacity));
    }

    /// <summary>
    /// Paint a square Mercator region of the quantised grid into BGRA.
    ///
    /// Pure, and public so it can be exercised without a graphics device: every output
    /// pixel is projected back into grid space, which avoids the holes a forward mapping
    /// leaves wherever the grid stretches. Level 0 is left fully transparent.
    /// </summary>
    public static byte[] RenderRegion(
        byte[] levels, Grib2Grid grid,
        double minX, double minY, double maxX, double maxY,
        int size, byte[] palette)
    {
        var bgra = new byte[size * size * 4];
        double span = maxX - minX;
        double northLat = grid.Lat1Deg;
        double westLon = grid.Lon1Deg;
        bool northToSouth = grid.NorthToSouth;

        Parallel.For(0, size, row =>
        {
            double my = maxY - (row + 0.5) / size * span;
            for (int column = 0; column < size; column++)
            {
                double mx = minX + (column + 0.5) / size * span;
                var (lat, lon) = GeoMath.FromMercator(mx, my);

                int ix = (int)((lon - westLon) / grid.DxDeg + 0.5);
                if (ix < 0 || ix >= grid.Nx) continue;

                double rows = northToSouth
                    ? (northLat - lat) / grid.DyDeg
                    : (lat - northLat) / grid.DyDeg;
                int iy = (int)(rows + 0.5);
                if (iy < 0 || iy >= grid.Ny) continue;

                byte level = levels[iy * grid.Nx + ix];
                if (level == 0) continue;

                int source = level * 4;
                int target = (row * size + column) * 4;
                bgra[target + 0] = palette[source + 2];
                bgra[target + 1] = palette[source + 1];
                bgra[target + 2] = palette[source + 0];
                bgra[target + 3] = palette[source + 3];
            }
        });
        return bgra;
    }

    public void Dispose()
    {
        _timer.Stop();
        _client.Dispose();
    }
}
