using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.NetCdf;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// The lightning layer: recent GLM flashes as crosses over the map, fading with age.
///
/// Lightning is the one thing on a radar display that is not inferred — reflectivity says
/// what the beam scattered off and velocity says how it moved, but a flash is a discharge
/// that actually happened, at a place and a time. It is also the fastest read on whether a
/// storm is intensifying, which is why every commercial viewer has it.
/// </summary>
public sealed class LightningController : IDisposable
{
    /// <summary>How far back to keep flashes. Ten minutes is the usual operational window.</summary>
    public const int WindowMinutes = 10;

    private readonly MapView _mapView;
    private readonly GlmClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private IReadOnlyList<LightningFlash> _flashes = [];
    private double _builtAtMetresPerPixel;
    private bool _busy;

    public OverlayGeometry? Geometry { get; private set; }

    public bool IsEnabled { get; private set; }

    public event Action? GeometryChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorRaised;

    public LightningController(MapView mapView)
    {
        _mapView = mapView;
        _timer.Tick += async (_, _) => await RefreshAsync();
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
        _flashes = [];
        Geometry = null;
        GeometryChanged?.Invoke();
    }

    /// <summary>Crosses are drawn at a fixed pixel size, so a zoom change means new geometry.</summary>
    public void NotifyViewChanged()
    {
        if (!IsEnabled || Geometry is null) return;
        double now = _mapView.Camera.Snapshot().MetersPerPixel;
        if (Math.Abs(now - _builtAtMetresPerPixel) / Math.Max(now, 1e-6) <= 0.05) return;
        Rebuild();
        GeometryChanged?.Invoke();
    }

    private async Task RefreshAsync()
    {
        if (!IsEnabled || _busy) return;
        _busy = true;
        try
        {
            var flashes = await _client.GetRecentAsync(WindowMinutes);
            if (!IsEnabled) return;

            _flashes = flashes;
            Rebuild();
            GeometryChanged?.Invoke();
            StatusChanged?.Invoke(flashes.Count == 0
                ? $"Lightning: no flashes in the last {WindowMinutes} minutes."
                : $"Lightning: {flashes.Count} flashes in the last {WindowMinutes} minutes.");
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke($"Could not fetch lightning: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// A cross per flash, sized in screen pixels and faded by age — the newest are white
    /// and opaque, the oldest a dim amber, so a glance separates what is happening now
    /// from what happened nine minutes ago.
    /// </summary>
    private void Rebuild()
    {
        double mpp = _mapView.Camera.Snapshot().MetersPerPixel;
        _builtAtMetresPerPixel = mpp;

        var geometry = new OverlayGeometry();
        var now = DateTime.UtcNow;
        double arm = 3.5 * mpp;

        foreach (var flash in _flashes)
        {
            double age = (now - flash.TimeUtc).TotalMinutes;
            if (age < 0) age = 0;
            if (age > WindowMinutes) continue;

            double freshness = 1.0 - age / WindowMinutes;
            byte alpha = (byte)Math.Clamp(70 + 185 * freshness, 0, 255);
            byte blue = (byte)Math.Clamp(60 + 195 * freshness, 0, 255);
            uint colour = OverlayGeometry.Pack(255, 240, blue, alpha);

            var (x, y) = GeoMath.ToMercator(flash.LatDeg, flash.LonDeg);
            geometry.Lines.Add(new OverlayLine(x - arm, y, x + arm, y, colour, 1.6f, LineCaps.Both));
            geometry.Lines.Add(new OverlayLine(x, y - arm, x, y + arm, colour, 1.6f, LineCaps.Both));
        }
        Geometry = geometry;
    }

    public void Dispose()
    {
        _timer.Stop();
        _client.Dispose();
    }
}
