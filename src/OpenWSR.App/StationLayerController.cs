using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// Surface observations drawn on the map: a dot per station with what it is reading.
///
/// <para>The radar says what the sky is doing; this says what the ground is. Over a squall
/// line the useful question is often not the reflectivity but whether the temperature has
/// dropped ten degrees behind it, and a field of station readings answers that at a glance in
/// a way no single number on the forecast page can.</para>
///
/// <para>It draws all three sources the forecast page knows about, coloured apart, and
/// **works with no keys at all** — the NWS station list is public, so the layer is useful the
/// moment it is ticked. Weather Underground stations and the user's own Ambient station join
/// it when their keys are set, which is the point of the layer: those are the ones that are
/// actually near you.</para>
/// </summary>
public sealed class StationLayerController : IDisposable, ITimedLayer
{
    /// <summary>
    /// Every station is a separate observation request on the NWS side — there is no bulk
    /// endpoint — so the count is capped rather than the whole list read. It also caps the
    /// clutter: fifty temperatures over one state is not a display, it is a wall.
    /// </summary>
    private const int NwsStations = 12;

    /// <summary>Personal stations are far denser than official ones, so fewer of them.</summary>
    private const int PersonalStations = 8;

    /// <summary>
    /// An ASOS reports every twenty minutes and a personal station every minute or so, so
    /// five minutes redraws something new without spending requests on unchanged numbers.
    /// </summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far the map may drift before the station list is fetched again. Stations are
    /// gathered around a point, so panning to the next state would otherwise show an empty
    /// map; refetching on every camera event would hammer the API instead.
    /// </summary>
    private const double RefetchAfterKm = 60;

    private readonly MapView _mapView;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer = new() { Interval = RefreshEvery };

    private ForecastClient? _nws;
    private PwsClient? _pws;
    private AmbientClient? _ambient;
    private string? _pwsKey;
    private (string?, string?) _ambientKeys;

    private IReadOnlyList<CurrentConditions> _readings = [];
    private double _builtAtMetresPerPixel;
    private (double LatDeg, double LonDeg)? _fetchedAt;
    private bool _busy;

    public OverlayGeometry? Geometry { get; private set; }

    public IReadOnlyList<MapView.MapLabel> Labels { get; private set; } = [];

    public bool IsEnabled { get; private set; }

    public event Action? GeometryChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorRaised;

    public StationLayerController(MapView mapView, AppSettings settings)
    {
        _mapView = mapView;
        _settings = settings;
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Enable()
    {
        IsEnabled = true;
        if (_pollingSuspended) return;   // hidden: this starts when the window comes back
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Disable()
    {
        IsEnabled = false;
        _timer.Stop();
        _readings = [];
        _fetchedAt = null;
        Geometry = null;
        Labels = [];
        GeometryChanged?.Invoke();
    }

    private bool _pollingSuspended;

    /// <inheritdoc />
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
        _ = RefreshAsync();
    }

    /// <summary>
    /// Markers are a fixed pixel size, so a zoom change means new geometry — and a large
    /// enough pan means a new set of stations altogether.
    /// </summary>
    public void NotifyViewChanged()
    {
        if (!IsEnabled) return;

        var cam = _mapView.Camera.Snapshot();
        var (lat, lon) = GeoMath.FromMercator(cam.CenterX, cam.CenterY);

        if (_fetchedAt is { } origin
            && GeoMath.DistanceM(origin.LatDeg, origin.LonDeg, lat, lon) / 1000.0 > RefetchAfterKm)
        {
            _ = RefreshAsync();
            return;
        }

        if (Geometry is null) return;
        double now = cam.MetersPerPixel;
        if (Math.Abs(now - _builtAtMetresPerPixel) / Math.Max(now, 1e-6) <= 0.05) return;
        Rebuild();
        GeometryChanged?.Invoke();
    }

    /// <summary>
    /// Rebuild the clients to match the keys in settings, so a key pasted into Settings takes
    /// effect on the next refresh rather than the next launch. Mirrors the forecast page.
    /// </summary>
    private void SyncClients()
    {
        _nws ??= new ForecastClient(_settings.UserAgent);

        var pwsKey = _settings.WeatherUndergroundKey;
        if (string.IsNullOrWhiteSpace(pwsKey))
        {
            _pws?.Dispose();
            _pws = null;
            _pwsKey = null;
        }
        else if (_pws is null || _pwsKey != pwsKey)
        {
            _pws?.Dispose();
            _pws = new PwsClient(pwsKey, _settings.UserAgent);
            _pwsKey = pwsKey;
        }

        if (!_settings.HasAmbientKeys)
        {
            _ambient?.Dispose();
            _ambient = null;
            _ambientKeys = default;
            return;
        }
        var keys = (_settings.AmbientApplicationKey, _settings.AmbientApiKey);
        if (_ambient is not null && _ambientKeys == keys) return;
        _ambient?.Dispose();
        _ambient = new AmbientClient(keys.Item1!, keys.Item2!, _settings.UserAgent);
        _ambientKeys = keys;
    }

    private async Task RefreshAsync()
    {
        if (!IsEnabled || _busy) return;
        _busy = true;
        try
        {
            SyncClients();

            var cam = _mapView.Camera.Snapshot();
            var (lat, lon) = GeoMath.FromMercator(cam.CenterX, cam.CenterY);

            var readings = new List<CurrentConditions>();
            var problems = new List<string>();

            // Each source is gathered independently, for the reason the forecast page had to
            // learn twice: one network being down must cost its own stations and nothing else.
            // Here that matters more, not less — the NWS half needs no key at all, so a
            // refused Weather Underground key taking the whole layer down would break the part
            // that cannot fail for that reason.
            foreach (var (name, gather) in Sources(lat, lon))
            {
                try
                {
                    readings.AddRange(await gather());
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Serilog.Log.Warning(e, "Station layer: {Source} unavailable", name);
                    problems.Add(name);
                }
            }
            if (!IsEnabled) return;

            _readings = readings;
            _fetchedAt = (lat, lon);
            Rebuild();
            GeometryChanged?.Invoke();
            StatusChanged?.Invoke(Summarise(readings, problems));
        }
        catch (Exception e)
        {
            ErrorRaised?.Invoke($"Could not fetch station observations: {e.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private IEnumerable<(string Name, Func<Task<IReadOnlyList<CurrentConditions>>> Gather)> Sources(
        double latDeg, double lonDeg)
    {
        yield return ("National Weather Service", () => NwsAsync(latDeg, lonDeg));
        if (_pws is not null) yield return ("Weather Underground", () => PersonalAsync(latDeg, lonDeg));
        if (_ambient is not null)
            yield return ("Ambient Weather",
                async () => await _ambient.GetDevicesAsync(latDeg, lonDeg));
    }

    private async Task<IReadOnlyList<CurrentConditions>> NwsAsync(double latDeg, double lonDeg)
    {
        var forecast = await _nws!.GetAsync(latDeg, lonDeg);
        var readings = new List<CurrentConditions>();

        foreach (var station in forecast.Stations.Take(NwsStations))
        {
            var reading = await _nws.GetObservationAsync(station);
            if (reading is { TemperatureC: not null }) readings.Add(reading);
        }
        return readings;
    }

    private async Task<IReadOnlyList<CurrentConditions>> PersonalAsync(double latDeg, double lonDeg)
    {
        var stations = await _pws!.NearbyAsync(latDeg, lonDeg);
        var readings = new List<CurrentConditions>();

        foreach (var station in stations.Where(s => s.PassedQualityCheck != false).Take(PersonalStations))
        {
            var reading = await _pws.GetObservationAsync(station);
            if (reading is { TemperatureC: not null }) readings.Add(reading);
        }
        return readings;
    }

    /// <summary>
    /// What the status line says. A count alone hides the interesting case — a key set but a
    /// network down — so a source that failed is named rather than silently missing.
    /// </summary>
    private static string Summarise(List<CurrentConditions> readings, List<string> problems)
    {
        // The scale is named here rather than on every marker: the labels are "73°" so that
        // they fit, and this is the one place that has room to say which 73 it is.
        var text = readings.Count == 0
            ? "Stations: nothing reporting near the middle of the map."
            : $"Stations: {readings.Count} reporting, {Units.TemperatureUnit}.";
        return problems.Count == 0 ? text : $"{text} No reply from {string.Join(" or ", problems)}.";
    }

    private void Rebuild()
    {
        double mpp = _mapView.Camera.Snapshot().MetersPerPixel;
        _builtAtMetresPerPixel = mpp;

        var (geometry, labels) = StationMarkers.Build(_readings, mpp);
        Geometry = geometry;
        Labels = labels;
    }

    public void Dispose()
    {
        _timer.Stop();
        _nws?.Dispose();
        _pws?.Dispose();
        _ambient?.Dispose();
    }
}

/// <summary>
/// Turning station readings into marks on the map. Static and free of the renderer so the
/// geometry can be asserted in a test rather than eyeballed in a screenshot.
/// </summary>
public static class StationMarkers
{
    /// <summary>Marker radius in screen pixels — a dot, not a symbol competing with the echo.</summary>
    private const double RadiusPx = 4.0;

    // Coloured by who runs the instrument, which is the same distinction the forecast page
    // draws in words. Official stations recede; the ones that are yours stand out, because a
    // reading from your own garden is the one you will act on.
    private static readonly uint NwsColour = OverlayGeometry.Pack(150, 190, 230, 220);
    private static readonly uint PersonalColour = OverlayGeometry.Pack(140, 220, 170, 230);
    private static readonly uint OwnColour = OverlayGeometry.Pack(255, 200, 90, 255);

    /// <summary>An outline dark enough to hold the dot against bright reflectivity under it.</summary>
    private static readonly uint OutlineColour = OverlayGeometry.Pack(10, 12, 16, 200);

    public static (OverlayGeometry Geometry, IReadOnlyList<MapView.MapLabel> Labels) Build(
        IReadOnlyList<CurrentConditions> readings, double metresPerPixel)
    {
        var geometry = new OverlayGeometry();
        var labels = new List<MapView.MapLabel>();
        double radius = RadiusPx * metresPerPixel;

        // Drawn official-first so a personal station, and then your own, land on top where two
        // sit close together — which near a house they routinely do.
        foreach (var reading in readings.OrderBy(r => r.Station.Source switch
        {
            StationSource.Nws => 0,
            StationSource.Personal => 1,
            _ => 2,
        }))
        {
            var (x, y) = GeoMath.ToMercator(reading.Station.LatDeg, reading.Station.LonDeg);

            AddDisc(geometry, x, y, radius, Colour(reading.Station.Source));
            StormOverlayController.AddCircle(geometry, (x, y), radius, OutlineColour, 1.2f);

            if (reading.TemperatureC is not { } celsius) continue;

            // Offset clear of the dot rather than centred on it, so the mark stays readable as
            // a position and the number does not sit on the thing it belongs to.
            labels.Add(new MapView.MapLabel(
                x, y, Units.TemperatureShort(celsius), (int)Math.Round(RadiusPx) + 4, 4));
        }
        return (geometry, labels);
    }

    private static uint Colour(StationSource source) => source switch
    {
        StationSource.Personal => PersonalColour,
        StationSource.Own => OwnColour,
        _ => NwsColour,
    };

    /// <summary>
    /// A filled dot. Twelve sides rather than the ninety-six a range ring uses: this is four
    /// pixels across, where the sagitta of a dodecagon is a hundredth of a pixel and the extra
    /// vertices would be spent on nothing, times fifty markers a frame.
    /// </summary>
    private static void AddDisc(OverlayGeometry g, double cx, double cy, double r, uint colour)
    {
        const int segments = 12;
        var ring = new (double X, double Y)[segments];
        for (int i = 0; i < segments; i++)
        {
            double a = 2 * Math.PI * i / segments;
            ring[i] = (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        g.AddPolygonFill(ring, colour);
    }
}
