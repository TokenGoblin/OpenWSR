using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.NetCdf;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// Native GOES ABI cloud imagery: fetched from the NOAA bucket, decoded, resampled into
/// Mercator and drawn under the radar.
///
/// This replaces the pre-rendered tile layer rather than joining it. The tiles were somebody
/// else's rendering of the same measurement, capped at their own resolution and served by a
/// path that has already moved once; going to the source buys the band, the enhancement and
/// the real five-minute cadence. The tile layer stays as the fallback for when the bucket is
/// unreachable, which is why <see cref="MapView.SatelliteEnabled"/> is still here.
/// </summary>
public sealed class SatelliteController : IDisposable
{
    /// <summary>
    /// Output raster edge. The CONUS sector is 2500 px across at 2 km; 2048 keeps most of
    /// that while holding the texture to 16 MB, and the imagery is a soft background layer
    /// where the last factor of resolution is not what anyone is reading.
    /// </summary>
    private const int RasterSize = 2048;

    /// <summary>Refresh a little faster than the five-minute scan so a new one is not missed.</summary>
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(4);

    private readonly MapView _mapView;
    private readonly AbiClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = Refresh };
    private int _generation;
    private bool _enabled;

    public SatelliteController(MapView mapView)
    {
        _mapView = mapView;
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>The scan time of what is on screen, or null when nothing is loaded.</summary>
    public DateTime? ValidUtc { get; private set; }

    public bool IsEnabled => _enabled;

    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorRaised;

    public void Enable()
    {
        if (_enabled) return;
        _enabled = true;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Disable()
    {
        _enabled = false;
        _timer.Stop();
        _generation++;                 // strand any fetch already in flight
        ValidUtc = null;
        _mapView.SetImageOverlay(MapView.OverlaySlot.Satellite, null);
    }

    public void SetOpacity(float opacity)
    {
        _opacity = Math.Clamp(opacity, 0f, 1f);
        if (_last is { } overlay)
            _mapView.SetImageOverlay(MapView.OverlaySlot.Satellite, overlay with { Opacity = _opacity });
    }

    private float _opacity = 0.6f;
    private MapView.ImageOverlay? _last;

    private async Task RefreshAsync()
    {
        int generation = ++_generation;
        try
        {
            StatusChanged?.Invoke("Satellite: fetching GOES-East infrared…");
            var product = await _client.GetLatestAsync(AbiClient.CleanInfrared);
            if (generation != _generation || !_enabled) return;
            if (product is null)
            {
                ErrorRaised?.Invoke(
                    "Satellite: no recent GOES imagery in the NOAA bucket. The tile layer is " +
                    "still available from the layers panel.");
                return;
            }

            var (overlay, time) = await Task.Run(() =>
            {
                var image = AbiFile.Decode(product.NetCdf);
                return (Rasterize(image, _opacity), image.TimeUtc);
            });
            if (generation != _generation || !_enabled) return;

            _last = overlay;
            ValidUtc = time;
            _mapView.SetImageOverlay(MapView.OverlaySlot.Satellite, overlay);

            double age = (DateTime.UtcNow - time).TotalMinutes;
            StatusChanged?.Invoke($"Satellite: GOES-East IR {time:HH:mm}Z ({age:F0} min old)");
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            ErrorRaised?.Invoke($"Satellite fetch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resample the fixed grid into Mercator by walking the output raster and projecting each
    /// pixel back — the same reverse mapping the HRRR raster and the 3D volume use, and for
    /// the same reason: a forward splat leaves holes wherever the source stretches, and this
    /// source stretches a great deal. A pixel over Canada covers several times the ground a
    /// pixel over the Gulf does, because the grid is angles from a camera rather than metres.
    /// </summary>
    internal static MapView.ImageOverlay Rasterize(AbiImage image, float opacity)
    {
        var projection = new Geostationary(
            image.Projection.PerspectivePointHeightM,
            image.Projection.SemiMajorM,
            image.Projection.SemiMinorM,
            image.Projection.SubSatelliteLonDeg,
            image.Projection.SweepX);

        // The grid is regular in scan angle, so a linear index into it inverts directly and
        // no search is needed per pixel.
        double x0 = image.ScanX[0];
        double y0 = image.ScanY[0];
        double dx = (image.ScanX[^1] - x0) / (image.Width - 1);
        double dy = (image.ScanY[^1] - y0) / (image.Height - 1);

        var (minX, minY, maxX, maxY) = MercatorBounds(image, projection);
        double spanX = maxX - minX, spanY = maxY - minY;

        var bgra = new byte[RasterSize * RasterSize * 4];
        Parallel.For(0, RasterSize, row =>
        {
            // Row 0 is the top of the image, which is maximum Mercator y.
            double my = maxY - (row + 0.5) / RasterSize * spanY;
            for (int column = 0; column < RasterSize; column++)
            {
                double mx = minX + (column + 0.5) / RasterSize * spanX;
                var (lat, lon) = GeoMath.FromMercator(mx, my);

                if (projection.Forward(lat, lon) is not { } scan) continue;
                int i = (int)Math.Round((scan.X - x0) / dx);
                int j = (int)Math.Round((scan.Y - y0) / dy);
                if (i < 0 || i >= image.Width || j < 0 || j >= image.Height) continue;

                float kelvin = image.Values[j * image.Width + i];
                if (float.IsNaN(kelvin)) continue;

                var (r, g, b, a) = InfraredColour(kelvin);
                if (a == 0) continue;
                int target = (row * RasterSize + column) * 4;
                bgra[target + 0] = b;
                bgra[target + 1] = g;
                bgra[target + 2] = r;
                bgra[target + 3] = a;
            }
        });

        return new MapView.ImageOverlay(
            bgra, RasterSize, RasterSize, minX, minY, maxX, maxY, opacity);
    }

    /// <summary>
    /// The Mercator box the sector covers.
    ///
    /// Taken from the edges rather than the four corners: the corners of a scan-angle
    /// rectangle are off the limb of the earth — the file stores fill there and the
    /// projection correctly refuses them — so a corner-based box is built from four
    /// non-places. Walking the borders finds the real extremes.
    /// </summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) MercatorBounds(
        AbiImage image, Geostationary projection)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Consider(double x, double y)
        {
            if (projection.Inverse(x, y) is not { } ground) return;
            var (mx, my) = GeoMath.ToMercator(ground.LatDeg, ground.LonDeg);
            minX = Math.Min(minX, mx); maxX = Math.Max(maxX, mx);
            minY = Math.Min(minY, my); maxY = Math.Max(maxY, my);
        }

        const int Samples = 64;
        for (int k = 0; k <= Samples; k++)
        {
            double fx = image.ScanX[0] + (image.ScanX[^1] - image.ScanX[0]) * k / Samples;
            double fy = image.ScanY[0] + (image.ScanY[^1] - image.ScanY[0]) * k / Samples;
            Consider(fx, image.ScanY[0]);
            Consider(fx, image.ScanY[^1]);
            Consider(image.ScanX[0], fy);
            Consider(image.ScanX[^1], fy);
        }

        if (minX > maxX || minY > maxY)
            throw new InvalidOperationException("No part of the satellite sector is on the earth.");
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// The infrared enhancement: grey for ordinary cloud, colour for the cold tops that
    /// matter.
    ///
    /// Warm ground is transparent rather than black. An IR image is a temperature field with
    /// a reading everywhere, so painting all of it would bury the basemap under a grey sheet
    /// — what a viewer wants from this layer is where the cloud is, and low cloud against
    /// warm ground is the part worth fading out. The colour ramp above the freezing level
    /// follows the convention every broadcast IR product uses: past about -40 °C the tops are
    /// glaciated and deep, and that is what should catch the eye.
    /// </summary>
    internal static (byte R, byte G, byte B, byte A) InfraredColour(float kelvin)
    {
        const float clearSky = 283f;   // ~10 °C: warm ground, nothing worth drawing
        const float cloudTop = 273f;   // freezing: solid grey by here

        if (kelvin >= clearSky) return (0, 0, 0, 0);

        if (kelvin >= 233f)
        {
            // 283 K down to -40 °C: transparent to opaque white, the ordinary cloud deck.
            float t = Math.Clamp((clearSky - kelvin) / (clearSky - 203f), 0f, 1f);
            byte level = (byte)(140 + 115 * Math.Clamp((clearSky - kelvin) / (clearSky - cloudTop), 0f, 1f));
            byte alpha = (byte)(255 * Math.Clamp(t * 2.2f, 0f, 1f));
            return (level, level, level, alpha);
        }

        // Colder than -40 °C: the enhancement everyone recognises, cyan through magenta.
        float u = Math.Clamp((233f - kelvin) / (233f - 183f), 0f, 1f);
        return u switch
        {
            < 0.25f => Ramp(u / 0.25f, (200, 200, 200), (0, 200, 255)),
            < 0.50f => Ramp((u - 0.25f) / 0.25f, (0, 200, 255), (0, 220, 90)),
            < 0.75f => Ramp((u - 0.50f) / 0.25f, (0, 220, 90), (255, 220, 0)),
            _ => Ramp((u - 0.75f) / 0.25f, (255, 220, 0), (255, 40, 200)),
        };
    }

    private static (byte R, byte G, byte B, byte A) Ramp(
        float t, (int R, int G, int B) from, (int R, int G, int B) to)
    {
        t = Math.Clamp(t, 0f, 1f);
        return ((byte)(from.R + (to.R - from.R) * t),
                (byte)(from.G + (to.G - from.G) * t),
                (byte)(from.B + (to.B - from.B) * t),
                255);
    }

    public void Dispose()
    {
        _timer.Stop();
        _client.Dispose();
    }
}
