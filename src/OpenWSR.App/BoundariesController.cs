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
/// draw lines that at national zoom are two hundred points to the pixel. <see cref="ShapeLayer"/>
/// carries the answer — project once, cull and thin per view — and rebuilds happen on a zoom
/// or a pan rather than on a frame.</para>
/// </remarks>
public sealed class BoundariesController : IDisposable
{
    private static readonly uint StateColor = OverlayGeometry.Pack(165, 185, 210, 215);
    private static readonly uint CountyColor = OverlayGeometry.Pack(120, 140, 165, 140);

    private readonly MapView _mapView;
    private readonly BoundaryClient _client;
    private readonly Dictionary<BoundarySet, ShapeLayer> _loaded = [];
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

    /// <summary>
    /// A set asked for could not be loaded and is no longer on.
    /// </summary>
    /// <remarks>
    /// The panel has to hear about this, or its checkbox stays ticked over a layer that is
    /// off and the only way to retry is to untick and retick.
    /// </remarks>
    public event Action<BoundarySet>? LoadFailed;

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

            var layer = await Task.Run(() => ShapeLayer.From(features));

            _loaded[set] = layer;
            StatusChanged?.Invoke(
                $"{(set == BoundarySet.States ? "State" : "County")} boundaries ready "
              + $"({layer.PartCount} outlines).");
            Rebuild();
        }
        catch (Exception ex)
        {
            if (set == BoundarySet.States) ShowStates = false;
            else ShowCounties = false;
            LoadFailed?.Invoke(set);
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
        // Per axis: the margin is a fraction of each viewport dimension, and on a pane
        // wider than it is tall a vertical pan would otherwise be measured against the
        // width and leave an unbuilt strip along the bottom edge indefinitely.
        double pannedX = Math.Abs(cam.CenterX - _builtAtCenterX)
                       / (cam.ViewportWidth * cam.MetersPerPixel);
        double pannedY = Math.Abs(cam.CenterY - _builtAtCenterY)
                       / (cam.ViewportHeight * cam.MetersPerPixel);

        if (!zoomed && Math.Max(pannedX, pannedY) < 0.25) return;
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

        var sets = new List<(ShapeLayer Layer, uint Colour, float Width)>();
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
            foreach (var (layer, colour, width) in sets)
                layer.Append(geometry, cam, colour, width);

            // A rebuild that the camera has already overtaken is thrown away rather than
            // drawn: it describes a view that is no longer on screen.
            if (generation != _buildGeneration) return;
            Geometry = geometry;
            GeometryChanged?.Invoke();
        });
    }

    public void Dispose() => _client.Dispose();
}
