using System.Diagnostics;
using OpenWSR.Geo;
using OpenWSR.Nexrad;
using OpenWSR.Palettes;
using OpenWSR.Render.Radar;
using OpenWSR.Render.Tiles;
using Vortice.Mathematics;

namespace OpenWSR.Render;

/// <summary>
/// Owns the render thread: camera, tile pipeline, and scene drawing. Input arrives on
/// the UI thread via the host control; the render thread owns all D3D objects.
/// </summary>
public sealed class MapView : IDisposable
{
    public MapCamera Camera { get; } = new();

    private readonly TileFetcher _fetcher;
    private Thread? _renderThread;
    private volatile bool _running;
    private IntPtr _hwnd;
    private volatile int _pendingWidth = 1;
    private volatile int _pendingHeight = 1;

    private readonly Lock _markerLock = new();
    private readonly List<(double MercX, double MercY)> _markers = [];

    // Drag/inertia state, touched only on the UI thread.
    private bool _dragging;
    private int _lastX, _lastY;
    private double _velX, _velY;
    private long _lastMoveTicks;

    public double FramesPerSecond { get; private set; }
    public double LastSweepUploadMs { get; private set; }

    /// <summary>Raised on the UI thread with the virtual-key code of keys pressed over the map.</summary>
    public event Action<int>? KeyPressed;

    internal void RaiseKeyPressed(int virtualKey) => KeyPressed?.Invoke(virtualKey);

    private readonly Lock _sweepLock = new();
    private (SweepGeometry Geometry, byte[] Palette, float Min, float Range)? _pendingSweep;
    private bool _sweepClearRequested;

    /// <summary>Stage a sweep for display; geometry prep runs on the calling thread.</summary>
    public void ShowSweep(Sweep sweep, ColorTable palette) =>
        ShowGeometry(SweepGeometry.Build(sweep), palette.BuildRgba256(), palette.MinValue, palette.Range);

    /// <summary>Stage prebuilt geometry (loop playback path — no CPU rebuild per frame).</summary>
    public void ShowGeometry(SweepGeometry geometry, byte[] paletteRgba256, float paletteMin, float paletteRange)
    {
        lock (_sweepLock)
        {
            _pendingSweep = (geometry, paletteRgba256, paletteMin, paletteRange);
            _sweepClearRequested = false;
        }
    }

    public void ClearSweep()
    {
        lock (_sweepLock)
        {
            _pendingSweep = null;
            _sweepClearRequested = true;
        }
    }

    public MapView(TileProvider provider)
    {
        _fetcher = new TileFetcher(provider);
    }

    public void Start(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _pendingWidth = Math.Max(1, width);
        _pendingHeight = Math.Max(1, height);
        Camera.SetViewport(_pendingWidth, _pendingHeight);
        _running = true;
        _renderThread = new Thread(RenderLoop) { Name = "OpenWSR.Render", IsBackground = true };
        _renderThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _renderThread?.Join(TimeSpan.FromSeconds(3));
        _renderThread = null;
    }

    public void Resize(int width, int height)
    {
        _pendingWidth = Math.Max(1, width);
        _pendingHeight = Math.Max(1, height);
        Camera.SetViewport(_pendingWidth, _pendingHeight);
    }

    public void SetMarkers(IEnumerable<(double LatDeg, double LonDeg)> positions)
    {
        lock (_markerLock)
        {
            _markers.Clear();
            foreach (var (lat, lon) in positions)
                _markers.Add(GeoMath.ToMercator(lat, lon));
        }
    }

    // ---- Input (UI thread) ----

    public void OnMouseDown(int x, int y)
    {
        _dragging = true;
        _lastX = x;
        _lastY = y;
        _velX = _velY = 0;
        _lastMoveTicks = Stopwatch.GetTimestamp();
        Camera.StopInertia();
    }

    public void OnMouseMove(int x, int y)
    {
        if (!_dragging) return;
        int dx = x - _lastX, dy = y - _lastY;
        _lastX = x;
        _lastY = y;
        Camera.PanPixels(dx, dy);

        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastMoveTicks) / (double)Stopwatch.Frequency;
        _lastMoveTicks = now;
        if (dt > 1e-4)
        {
            const double smoothing = 0.4;
            _velX = smoothing * (dx / dt) + (1 - smoothing) * _velX;
            _velY = smoothing * (dy / dt) + (1 - smoothing) * _velY;
        }
    }

    public void OnMouseUp()
    {
        if (!_dragging) return;
        _dragging = false;
        double sinceMove = (Stopwatch.GetTimestamp() - _lastMoveTicks) / (double)Stopwatch.Frequency;
        if (sinceMove < 0.15)
            Camera.SetInertia(_velX, _velY);
    }

    public void OnMouseWheel(int clientX, int clientY, int delta) =>
        Camera.ZoomAt(clientX, clientY, Math.Pow(1.25, delta / 120.0));

    // ---- Render thread ----

    private void RenderLoop()
    {
        using var device = new DeviceResources(_hwnd, _pendingWidth, _pendingHeight);
        using var textures = new TileTextureCache(device.Device);
        using var quads = new QuadRenderer(device.Device, device.Context);
        using var radar = new RadarSweepRenderer(device.Device, device.Context);

        var clearColor = new Color4(0.10f, 0.11f, 0.13f, 1f);
        long lastTicks = Stopwatch.GetTimestamp();
        long fpsWindowStart = lastTicks;
        int fpsFrames = 0;

        while (_running)
        {
            if (_pendingWidth != device.Width || _pendingHeight != device.Height)
                device.Resize(_pendingWidth, _pendingHeight);

            long now = Stopwatch.GetTimestamp();
            double dt = (now - lastTicks) / (double)Stopwatch.Frequency;
            lastTicks = now;
            Camera.Tick(Math.Min(dt, 0.1));

            // Upload any tiles the fetcher completed since last frame.
            while (_fetcher.TryDequeueCompleted(out var done))
                textures.Add(done.Key, done.Bgra);

            var cam = Camera.Snapshot();
            var ctx = device.Context;
            textures.BeginFrame();

            ctx.OMSetRenderTargets(device.BackBufferView!);
            ctx.RSSetViewport(0, 0, device.Width, device.Height);
            ctx.ClearRenderTargetView(device.BackBufferView!, clearColor);

            quads.Begin();
            DrawTiles(cam, textures, quads);

            lock (_sweepLock)
            {
                if (_pendingSweep is not null)
                {
                    var (geometry, palette, min, range) = _pendingSweep.Value;
                    radar.SetSweep(geometry, palette, min, range);
                    _pendingSweep = null;
                }
                else if (_sweepClearRequested)
                {
                    radar.Clear();
                    _sweepClearRequested = false;
                }
            }
            radar.Draw(cam);
            LastSweepUploadMs = radar.LastUploadMs;

            quads.Begin();
            DrawMarkers(cam, quads);

            device.SwapChain.Present(1);

            fpsFrames++;
            if (now - fpsWindowStart >= Stopwatch.Frequency)
            {
                FramesPerSecond = fpsFrames / ((now - fpsWindowStart) / (double)Stopwatch.Frequency);
                fpsFrames = 0;
                fpsWindowStart = now;
            }
        }
    }

    private void DrawTiles(CameraSnapshot cam, TileTextureCache textures, QuadRenderer quads)
    {
        int zoom = TileMath.ZoomForMetersPerPixel(cam.MetersPerPixel);
        var (minX, minY, maxX, maxY) = cam.WorldBounds();

        foreach (var key in TileMath.Cover(minX, minY, maxX, maxY, zoom))
        {
            var bounds = TileMath.Bounds(key);
            var clip = cam.ToClip(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);

            if (textures.TryGet(key, out var view))
            {
                quads.DrawTextured(clip, (0, 0, 1, 1), view);
                continue;
            }

            _fetcher.Request(key);

            // Stretch the nearest cached ancestor over this tile while it loads.
            var ancestor = key;
            for (int up = 0; up < 6 && ancestor.Z > 0; up++)
            {
                ancestor = ancestor.Parent;
                if (!textures.TryGet(ancestor, out var ancestorView))
                    continue;
                int levels = key.Z - ancestor.Z;
                float size = 1f / (1 << levels);
                float u0 = (key.X - (ancestor.X << levels)) * size;
                float v0 = (key.Y - (ancestor.Y << levels)) * size;
                quads.DrawTextured(clip, (u0, v0, u0 + size, v0 + size), ancestorView);
                break;
            }
        }
    }

    private void DrawMarkers(CameraSnapshot cam, QuadRenderer quads)
    {
        (double X, double Y)[] markers;
        lock (_markerLock)
        {
            markers = [.. _markers];
        }
        double half = 4 * cam.MetersPerPixel; // 8 px squares
        foreach (var (mx, my) in markers)
        {
            var clip = cam.ToClip(mx - half, my - half, mx + half, my + half);
            if (clip.X1 < -1 || clip.X0 > 1 || clip.Y1 < -1 || clip.Y0 > 1) continue;
            quads.DrawSolid(clip, 0.95f, 0.15f, 0.15f, 0.9f);
        }
    }

    public void Dispose()
    {
        Stop();
        _fetcher.Dispose();
    }
}
