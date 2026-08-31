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
public sealed class SatelliteController : IDisposable, ITimedLayer
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
        if (_pollingSuspended) return;   // hidden: this starts when the window comes back
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
        if (!IsEnabled) return;
        _timer.Start();
        _ = RefreshAsync();   // what it holds is at least as old as the pause
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
        bool daylit = AnyDaylightOverConus(DateTime.UtcNow);
        try
        {
            StatusChanged?.Invoke(daylit
                ? "Satellite: fetching GOES-East imagery…"
                : "Satellite: fetching GOES-East infrared…");

            // At night the reflective bands are noise, so there is nothing to fetch them for:
            // one band is 3 MB against 40-plus for all sixteen. This is the same decision the
            // blend makes per pixel, taken once for the whole download.
            var product = daylit
                ? await _client.GetLatestMultibandAsync()
                : await _client.GetLatestAsync(AbiClient.CleanInfrared);
            if (generation != _generation || !_enabled) return;
            if (product is null)
            {
                ErrorRaised?.Invoke(
                    "Satellite: no recent GOES imagery in the NOAA bucket. The tile layer is " +
                    "still available from the layers panel.");
                return;
            }

            var (overlay, time, lit) = await Task.Run(() =>
            {
                if (!daylit)
                {
                    var infrared = AbiFile.Decode(product.NetCdf);
                    return (Rasterize(infrared, null, _opacity), infrared.TimeUtc, false);
                }

                var bands = AbiFile.DecodeMultiband(product.NetCdf, 1, 2, 3, AbiClient.CleanInfrared);
                var visible = new VisibleBands(
                    bands[1].Values, bands[2].Values, bands[3].Values);
                var image = bands[AbiClient.CleanInfrared];
                return (Rasterize(image, visible, _opacity), image.TimeUtc, true);
            });
            if (generation != _generation || !_enabled) return;

            _last = overlay;
            ValidUtc = time;
            _mapView.SetImageOverlay(MapView.OverlaySlot.Satellite, overlay);

            double age = (DateTime.UtcNow - time).TotalMinutes;
            StatusChanged?.Invoke(
                $"Satellite: GOES-East {(lit ? "visible + IR" : "IR")} {time:HH:mm}Z ({age:F0} min old)");
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            ErrorRaised?.Invoke($"Satellite fetch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether any part of the sector is lit well enough for the reflective bands to say
    /// anything.
    ///
    /// Sampled across CONUS rather than taken at its centre: the sector spans about three
    /// hours of longitude, so around dawn and dusk one edge is in daylight while the other is
    /// still dark, and a centre reading would drop the visible bands for the half of the
    /// country that still has them.
    /// </summary>
    internal static bool AnyDaylightOverConus(DateTime utc)
    {
        for (double lat = 25; lat <= 49; lat += 8)
            for (double lon = -125; lon <= -67; lon += 8)
                if (SolarPosition.DaylightFraction(SolarPosition.ZenithDeg(lat, lon, utc)) > 0.01)
                    return true;
        return false;
    }

    /// <summary>
    /// Resample the fixed grid into Mercator by walking the output raster and projecting each
    /// pixel back — the same reverse mapping the HRRR raster and the 3D volume use, and for
    /// the same reason: a forward splat leaves holes wherever the source stretches, and this
    /// source stretches a great deal. A pixel over Canada covers several times the ground a
    /// pixel over the Gulf does, because the grid is angles from a camera rather than metres.
    /// </summary>
    internal static MapView.ImageOverlay Rasterize(AbiImage image, float opacity) =>
        Rasterize(image, null, opacity);

    /// <summary>
    /// The same resample, compositing the reflective bands over the infrared where the sun is
    /// up. <paramref name="visible"/> is null at night or when only the infrared was fetched.
    /// </summary>
    internal static MapView.ImageOverlay Rasterize(
        AbiImage image, VisibleBands? visible, float opacity)
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

                int index = j * image.Width + i;
                float kelvin = image.Values[index];
                if (float.IsNaN(kelvin)) continue;

                var (r, g, b, a) = InfraredColour(kelvin);

                // Where the sun is up, cross-fade to what the reflective bands see. The two
                // halves are different measurements — emitted heat and reflected sunlight —
                // and the daylight fraction is the only honest way to weigh them.
                if (visible is { } bands)
                {
                    double daylight = SolarPosition.DaylightFraction(
                        SolarPosition.ZenithDeg(lat, lon, image.TimeUtc));
                    if (daylight > 0)
                    {
                        var day = VisibleColour(bands, index);
                        r = Mix(r, day.R, daylight);
                        g = Mix(g, day.G, daylight);
                        b = Mix(b, day.B, daylight);
                        a = Mix(a, day.A, daylight);
                    }
                }

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

    /// <summary>The three reflective bands of one scan, already on a shared grid.</summary>
    internal sealed record VisibleBands(float[] Blue, float[] Red, float[] Veggie);

    /// <summary>
    /// Daytime colour: what the eye would see, with clear ground left transparent.
    ///
    /// This departs from CIRA's GeoColor deliberately, and the reason is what sits underneath.
    /// In GeoColor the satellite <em>is</em> the base image, so painting the whole earth is
    /// the point. Here it is an overlay on a real vector basemap — roads, boundaries, terrain,
    /// the things a radar viewer is read against — and painting true-colour land over that
    /// hides the map. So the colour is true-colour where there is something in the air and the
    /// alpha falls away over clear ground.
    ///
    /// ABI has no green detector. Green is synthesised from the other three as
    /// 0.45·red + 0.10·veggie + 0.45·blue, the hybrid the GOES-R community settled on: a
    /// straight red/blue average leaves vegetation an unconvincing brown, and leaning harder
    /// on the 0.86 µm band turns forests luminous green.
    /// </summary>
    internal static (byte R, byte G, byte B, byte A) VisibleColour(VisibleBands bands, int index)
    {
        float blue = bands.Blue[index], red = bands.Red[index], veggie = bands.Veggie[index];
        if (float.IsNaN(blue) || float.IsNaN(red) || float.IsNaN(veggie)) return (0, 0, 0, 0);

        float green = 0.45f * red + 0.10f * veggie + 0.45f * blue;

        // Reflectance is linear in radiance; eyes are not. The square-root stretch is what
        // stops everything but cloud tops reading as near-black.
        byte r = Stretch(red), g = Stretch(green), b = Stretch(blue);

        // Cloud is what is worth drawing, and cloud is bright: ground reflectance runs to
        // about 0.2 over vegetation and 0.35 over desert, where cloud goes well past 0.5.
        const float clearGround = 0.22f, solidCloud = 0.55f;
        float brightness = 0.4f * red + 0.4f * blue + 0.2f * veggie;
        float bright = Math.Clamp((brightness - clearGround) / (solidCloud - clearGround), 0f, 1f);

        // Brightness alone is not enough, because bright desert clears that bar. Cloud is also
        // spectrally *flat* — it scatters all three wavelengths about equally, which is why it
        // looks white — while ground is not: desert is markedly redder than it is blue, and
        // vegetation is several times brighter at 0.86 µm than at 0.64. Weighting by how
        // neutral the pixel is separates the two, and it is the same reasoning behind the
        // colour being kept at all rather than everything cloud-like being painted white.
        //
        // Snow survives this, being genuinely bright and genuinely neutral. That is a real
        // ambiguity in the measurement rather than a shortcut here — snow and cloud look alike
        // to these three bands — and it shows up as a fainter wash rather than solid cloud.
        float peak = Math.Max(red, Math.Max(blue, veggie));
        float trough = Math.Min(red, Math.Min(blue, veggie));
        float neutrality = peak <= 0 ? 0 : 1f - (peak - trough) / peak;
        float flat = Math.Clamp((neutrality - 0.55f) / (0.95f - 0.55f), 0f, 1f);

        return (r, g, b, (byte)(255 * bright * flat));
    }

    /// <summary>Gamma 2.2, which is the sRGB curve the value is about to be shown through.</summary>
    private static byte Stretch(float reflectance) =>
        (byte)(255 * Math.Clamp(MathF.Pow(Math.Clamp(reflectance, 0f, 1f), 1f / 2.2f), 0f, 1f));

    private static byte Mix(byte night, byte day, double daylight) =>
        (byte)Math.Clamp(night + (day - night) * daylight, 0, 255);

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
