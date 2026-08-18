using OpenWSR.Geo;

namespace OpenWSR.Render;

/// <summary>
/// Orthographic camera over Web Mercator space. Mutated from the UI thread (input)
/// and read from the render thread — all access goes through the lock.
/// </summary>
public sealed class MapCamera
{
    private readonly Lock _lock = new();
    private double _centerX;
    private double _centerY;
    private double _metersPerPixel = 5000;
    private int _viewportWidth = 1;
    private int _viewportHeight = 1;

    // Inertia state: velocity in pixels/second, applied and decayed by the render tick.
    private double _velocityX;
    private double _velocityY;

    public const double MinMetersPerPixel = 0.3;   // ~z19
    public const double MaxMetersPerPixel = 80000; // whole hemisphere

    public void SetViewport(int widthPx, int heightPx)
    {
        lock (_lock)
        {
            _viewportWidth = Math.Max(1, widthPx);
            _viewportHeight = Math.Max(1, heightPx);
        }
    }

    public void MoveTo(double latDeg, double lonDeg, double metersPerPixel)
    {
        var (x, y) = GeoMath.ToMercator(latDeg, lonDeg);
        lock (_lock)
        {
            _centerX = x;
            _centerY = y;
            _metersPerPixel = Math.Clamp(metersPerPixel, MinMetersPerPixel, MaxMetersPerPixel);
            _velocityX = _velocityY = 0;
        }
    }

    public void PanPixels(double dxPx, double dyPx)
    {
        lock (_lock)
        {
            _centerX -= dxPx * _metersPerPixel;
            _centerY += dyPx * _metersPerPixel; // screen y grows downward
            ClampCenter();
        }
    }

    /// <summary>Zoom by a factor keeping the world point under the cursor stationary.</summary>
    public void ZoomAt(double cursorXPx, double cursorYPx, double factor)
    {
        lock (_lock)
        {
            double newMpp = Math.Clamp(_metersPerPixel / factor, MinMetersPerPixel, MaxMetersPerPixel);
            double actual = _metersPerPixel / newMpp;
            if (Math.Abs(actual - 1) < 1e-9) return;

            double worldX = _centerX + (cursorXPx - _viewportWidth / 2.0) * _metersPerPixel;
            double worldY = _centerY - (cursorYPx - _viewportHeight / 2.0) * _metersPerPixel;
            _metersPerPixel = newMpp;
            _centerX = worldX - (cursorXPx - _viewportWidth / 2.0) * _metersPerPixel;
            _centerY = worldY + (cursorYPx - _viewportHeight / 2.0) * _metersPerPixel;
            ClampCenter();
        }
    }

    public void SetInertia(double velocityXPxPerSec, double velocityYPxPerSec)
    {
        lock (_lock)
        {
            _velocityX = velocityXPxPerSec;
            _velocityY = velocityYPxPerSec;
        }
    }

    public void StopInertia() => SetInertia(0, 0);

    /// <summary>Advance inertia by dt; returns true while still moving (needs redraw).</summary>
    public bool Tick(double dtSeconds)
    {
        lock (_lock)
        {
            if (Math.Abs(_velocityX) < 5 && Math.Abs(_velocityY) < 5)
            {
                _velocityX = _velocityY = 0;
                return false;
            }
            _centerX -= _velocityX * dtSeconds * _metersPerPixel;
            _centerY += _velocityY * dtSeconds * _metersPerPixel;
            ClampCenter();
            double decay = Math.Pow(0.05, dtSeconds); // ~95% gone after 1 s
            _velocityX *= decay;
            _velocityY *= decay;
            return true;
        }
    }

    public CameraSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new CameraSnapshot(_centerX, _centerY, _metersPerPixel, _viewportWidth, _viewportHeight);
        }
    }

    private void ClampCenter()
    {
        _centerX = Math.Clamp(_centerX, -GeoMath.MercatorExtentM, GeoMath.MercatorExtentM);
        _centerY = Math.Clamp(_centerY, -GeoMath.MercatorExtentM, GeoMath.MercatorExtentM);
    }
}

/// <summary>Immutable per-frame camera state used by the render thread.</summary>
public readonly record struct CameraSnapshot(
    double CenterX, double CenterY, double MetersPerPixel, int ViewportWidth, int ViewportHeight)
{
    public (double MinX, double MinY, double MaxX, double MaxY) WorldBounds()
    {
        double halfW = ViewportWidth * MetersPerPixel / 2.0;
        double halfH = ViewportHeight * MetersPerPixel / 2.0;
        return (CenterX - halfW, CenterY - halfH, CenterX + halfW, CenterY + halfH);
    }

    /// <summary>World (Mercator) rectangle to normalized device coordinates.</summary>
    public (float X0, float Y0, float X1, float Y1) ToClip(double minX, double minY, double maxX, double maxY)
    {
        double halfW = ViewportWidth * MetersPerPixel / 2.0;
        double halfH = ViewportHeight * MetersPerPixel / 2.0;
        return (
            (float)((minX - CenterX) / halfW),
            (float)((minY - CenterY) / halfH),
            (float)((maxX - CenterX) / halfW),
            (float)((maxY - CenterY) / halfH));
    }
}
