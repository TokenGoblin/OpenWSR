using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// Draws state and county lines as a layer of their own.
/// </summary>
/// <remarks>
/// The basemap already carries boundaries, but baked into the tile: they cannot be turned
/// off when they clutter a velocity field, cannot be brightened when an echo covers them,
/// and vanish entirely if the provider is switched to one that omits them. A county line is
/// how a warning is described — "northern Cleveland County until 7:15" — so it needs to be a
/// thing the app controls rather than a thing it inherits.
///
/// <para><b>The geometry is far too big to draw literally.</b> The 1:500,000 county file is
/// 1.03 million points; as overlay segments that is over six million vertices a frame, to
/// draw lines that at national zoom are two hundred points to the pixel. So each rebuild
/// culls to the viewport and then simplifies what is left to a screen-space tolerance, and
/// rebuilds happen on a zoom change rather than a frame. Projection and bounds are computed
/// once at load, because those do not depend on the view and reprojecting a million points
/// per zoom step would cost more than the simplification does.</para>
/// </remarks>
public sealed class BoundariesController : IDisposable
{
    /// <summary>
    /// How far a simplified line may sit from the true one, in screen pixels. Below about a
    /// pixel there is nothing to see; above about two the corners of a county visibly move.
    /// </summary>
    private const double ToleranceP2x = 1.2;

    private static readonly uint StateColor = OverlayGeometry.Pack(165, 185, 210, 215);
    private static readonly uint CountyColor = OverlayGeometry.Pack(120, 140, 165, 140);

    private sealed record Part(
        (double X, double Y)[] Points,
        double MinX, double MinY, double MaxX, double MaxY);

    private readonly MapView _mapView;
    private readonly BoundaryClient _client;
    private readonly Dictionary<BoundarySet, List<Part>> _loaded = [];
    private readonly HashSet<BoundarySet> _loading = [];

    private double _builtAtMetresPerPixel;
    private double _builtAtCenterX, _builtAtCenterY;
    private int _buildGeneration;

    public BoundariesController(MapView mapView, string userAgent)
    {
        _mapView = mapView;
        _client = new BoundaryClient(userAgent);
    }

    public bool ShowStates { get; private set; }
    public bool ShowCounties { get; private set; }

    public OverlayGeometry? Geometry { get; private set; }

    public event Action? GeometryChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorRaised;

    /// <summary>True when the set is on disk, so turning it on will not hit the network.</summary>
    public bool IsCached(BoundarySet set) => _client.IsCached(set);

    public void SetVisible(BoundarySet set, bool visible)
    {
        if (set == BoundarySet.States) ShowStates = visible;
        else ShowCounties = visible;

        if (!visible)
        {
            Rebuild();
            return;
        }
        if (_loaded.ContainsKey(set)) Rebuild();
        else _ = LoadAsync(set);
    }

    private async Task LoadAsync(BoundarySet set)
    {
        if (!_loading.Add(set)) return;
        try
        {
            var features = await _client.GetAsync(
                set, new Progress<string>(text => StatusChanged?.Invoke(text)));

            // Project once. Everything after this is culling and thinning, which are cheap
            // by comparison and are the only parts that depend on where the camera is.
            var parts = await Task.Run(() =>
            {
                var built = new List<Part>();
                foreach (var feature in features)
                    foreach (var ring in feature.Parts)
                    {
                        if (ring.Count < 2) continue;
                        var points = new (double X, double Y)[ring.Count];
                        for (int i = 0; i < ring.Count; i++)
                            points[i] = GeoMath.ToMercator(ring[i].LatDeg, ring[i].LonDeg);
                        var (minX, minY, maxX, maxY) = Polyline.Bounds(points);
                        built.Add(new Part(points, minX, minY, maxX, maxY));
                    }
                return built;
            });

            _loaded[set] = parts;
            StatusChanged?.Invoke(
                $"{(set == BoundarySet.States ? "State" : "County")} boundaries ready "
              + $"({parts.Count} outlines).");
            Rebuild();
        }
        catch (Exception ex)
        {
            if (set == BoundarySet.States) ShowStates = false;
            else ShowCounties = false;
            ErrorRaised?.Invoke($"Could not load boundaries: {ex.Message}");
        }
        finally
        {
            _loading.Remove(set);
        }
    }

    /// <summary>
    /// Rebuild when the view has moved enough to matter — a different zoom needs a different
    /// tolerance, and a pan brings different counties into the viewport.
    /// </summary>
    public void NotifyViewChanged()
    {
        // Not "Geometry is null" — the first build can land before the map host has sized
        // its viewport, cull everything, and produce nothing. Bailing on that would strand
        // the layer permanently.
        if (!ShowStates && !ShowCounties) return;
        if (_loaded.Count == 0) return;
        var cam = _mapView.Camera.Snapshot();
        if (cam.ViewportWidth <= 0 || cam.ViewportHeight <= 0) return;

        bool zoomed = Math.Abs(cam.MetersPerPixel - _builtAtMetresPerPixel)
                      / Math.Max(cam.MetersPerPixel, 1e-6) > 0.05;
        double movedX = Math.Abs(cam.CenterX - _builtAtCenterX);
        double movedY = Math.Abs(cam.CenterY - _builtAtCenterY);
        double panned = Math.Max(movedX, movedY) / (cam.ViewportWidth * cam.MetersPerPixel);

        if (!zoomed && panned < 0.25) return;
        Rebuild();
    }

    private void Rebuild()
    {
        if (!ShowStates && !ShowCounties)
        {
            Geometry = null;
            GeometryChanged?.Invoke();
            return;
        }

        var cam = _mapView.Camera.Snapshot();
        _builtAtMetresPerPixel = cam.MetersPerPixel;
        _builtAtCenterX = cam.CenterX;
        _builtAtCenterY = cam.CenterY;

        // A margin so a small pan does not immediately show an unbuilt edge.
        double halfW = cam.ViewportWidth * cam.MetersPerPixel * 0.75;
        double halfH = cam.ViewportHeight * cam.MetersPerPixel * 0.75;
        double minX = cam.CenterX - halfW, maxX = cam.CenterX + halfW;
        double minY = cam.CenterY - halfH, maxY = cam.CenterY + halfH;
        double tolerance = ToleranceP2x * cam.MetersPerPixel;

        var sets = new List<(List<Part> Parts, uint Colour, float Width)>();
        if (ShowCounties && _loaded.TryGetValue(BoundarySet.Counties, out var counties))
            sets.Add((counties, CountyColor, 1.1f));
        if (ShowStates && _loaded.TryGetValue(BoundarySet.States, out var states))
            sets.Add((states, StateColor, 1.8f));
        if (sets.Count == 0)
        {
            Geometry = null;
            GeometryChanged?.Invoke();
            return;
        }

        int generation = ++_buildGeneration;
        _ = Task.Run(() =>
        {
            var geometry = new OverlayGeometry();
            foreach (var (parts, colour, width) in sets)
                foreach (var part in parts)
                {
                    if (part.MaxX < minX || part.MinX > maxX ||
                        part.MaxY < minY || part.MinY > maxY) continue;

                    var thinned = Polyline.Simplify(part.Points, tolerance);
                    for (int i = 0; i + 1 < thinned.Count; i++)
                        geometry.Lines.Add((
                            thinned[i].X, thinned[i].Y,
                            thinned[i + 1].X, thinned[i + 1].Y, colour, width));
                }

            // A rebuild that the camera has already overtaken is thrown away rather than
            // drawn: it describes a view that is no longer on screen.
            if (generation != _buildGeneration) return;
            Geometry = geometry;
            GeometryChanged?.Invoke();
        });
    }

    public void Dispose() => _client.Dispose();
}
