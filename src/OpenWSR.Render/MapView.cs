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
    private int _dragStartX, _dragStartY;
    private double _velX, _velY;
    private long _lastMoveTicks;

    public double FramesPerSecond { get; private set; }
    public double LastSweepUploadMs { get; private set; }

    private volatile float _radarOpacity = 0.85f;
    private volatile float _radarSmoothing;

    /// <summary>Radar layer opacity, 0–1. Written from the UI thread, read per frame.</summary>
    public float RadarOpacity
    {
        get => _radarOpacity;
        set => _radarOpacity = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Smoothing strength 0–1: raw gates → soft consumer-style blur.</summary>
    public float RadarSmoothing
    {
        get => _radarSmoothing;
        set => _radarSmoothing = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Raised on the UI thread with the virtual-key code of keys pressed over the map.</summary>
    public event Action<int>? KeyPressed;

    /// <summary>Cursor motion over the map while not dragging (UI thread, client pixels).</summary>
    public event Action<int, int>? Hovered;

    /// <summary>A click that wasn't a drag (UI thread, client pixels).</summary>
    public event Action<int, int>? Clicked;

    /// <summary>Right-button measure drag: (startX, startY, x, y, finished).</summary>
    public event Action<int, int, int, int, bool>? MeasureDragged;

    private bool _rightDragging;
    private int _rightStartX, _rightStartY;

    public void OnRightDown(int x, int y)
    {
        _rightDragging = true;
        _rightStartX = x;
        _rightStartY = y;
    }

    public void OnRightMove(int x, int y)
    {
        if (_rightDragging)
            MeasureDragged?.Invoke(_rightStartX, _rightStartY, x, y, false);
    }

    public void OnRightUp(int x, int y)
    {
        if (!_rightDragging) return;
        _rightDragging = false;
        MeasureDragged?.Invoke(_rightStartX, _rightStartY, x, y, true);
    }

    internal void RaiseKeyPressed(int virtualKey) => KeyPressed?.Invoke(virtualKey);

    private readonly Lock _overlayLock = new();
    private OverlayGeometry? _overlay;
    private readonly GlyphAtlas _glyphAtlas = new(); // built on the UI thread (WPF)
    private IReadOnlyList<MapLabel> _labels = [];

    /// <summary>A short text label anchored at a Mercator position, offset in pixels.</summary>
    public readonly record struct MapLabel(
        double MercX, double MercY, string Text, int OffsetXPx, int OffsetYPx);

    /// <summary>Replace the label layer (storm IDs, dBZ). Swapped atomically.</summary>
    public void SetLabels(IReadOnlyList<MapLabel> labels) => _labels = labels;

    /// <summary>A pre-rendered BGRA image pinned to a Mercator rectangle.</summary>
    public sealed record ImageOverlay(
        byte[] Bgra, int Width, int Height,
        double MinX, double MinY, double MaxX, double MaxY, float Opacity);

    private ImageOverlay? _imageOverlay;
    private ImageOverlay? _uploadedImage;
    private Vortice.Direct3D11.ID3D11Texture2D? _imageTexture;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _imageView;

    /// <summary>
    /// Show a georeferenced raster over the basemap — model output or any gridded field
    /// already resampled into Web Mercator. Null clears it.
    /// </summary>
    public void SetImageOverlay(ImageOverlay? overlay) => _imageOverlay = overlay;

    private readonly Lock _captureLock = new();
    private (byte[] Bgra, int Width, int Height)? _capture;
    private volatile bool _captureRequested;

    /// <summary>
    /// Read back what the map is currently showing. The request is served by the render
    /// thread after the next present, so the caller always gets a complete frame.
    /// </summary>
    public (byte[] Bgra, int Width, int Height)? CaptureFrame(int timeoutMs = 1500)
    {
        lock (_captureLock) _capture = null;
        _captureRequested = true;

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_captureLock)
            {
                if (_capture is { } ready) return ready;
            }
            Thread.Sleep(15);
        }
        return null;
    }

    private unsafe void ServiceCaptureRequest(DeviceResources device)
    {
        if (!_captureRequested) return;
        _captureRequested = false;
        try
        {
            using var backBuffer = device.SwapChain.GetBuffer<Vortice.Direct3D11.ID3D11Texture2D>(0);
            var desc = backBuffer.Description;
            desc.Usage = Vortice.Direct3D11.ResourceUsage.Staging;
            desc.BindFlags = Vortice.Direct3D11.BindFlags.None;
            desc.CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read;
            desc.MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None;

            using var staging = device.Device.CreateTexture2D(desc);
            device.Context.CopyResource(staging, backBuffer);

            var mapped = device.Context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read);
            int width = (int)desc.Width, height = (int)desc.Height;
            var bytes = new byte[width * height * 4];
            byte* source = (byte*)mapped.DataPointer;
            for (int row = 0; row < height; row++)
            {
                fixed (byte* destination = &bytes[row * width * 4])
                    Buffer.MemoryCopy(source + row * mapped.RowPitch, destination, width * 4, width * 4);
            }
            device.Context.Unmap(staging, 0);

            // The swapchain ignores alpha; force it opaque so saved images are not blank.
            for (int i = 3; i < bytes.Length; i += 4) bytes[i] = 0xFF;
            lock (_captureLock) _capture = (bytes, width, height);
        }
        catch (Exception)
        {
            lock (_captureLock) _capture = null;
        }
    }

    private sealed record LegendSpec(byte[] Rgba, float Min, float Max, string Title);

    private LegendSpec? _legend;
    private LegendSpec? _uploadedLegend;

    /// <summary>
    /// Show a colour scale for the displayed product. Drawn in the D3D scene rather than
    /// in WPF, because the child window always covers WPF content in its rectangle.
    /// </summary>
    public void SetLegend(byte[] paletteRgba256, float min, float max, string title) =>
        _legend = new LegendSpec(paletteRgba256, min, max, title);

    public void ClearLegend() => _legend = null;

    /// <summary>Replace the overlay layer (warning polygons, measure lines). Null clears.</summary>
    public void SetOverlay(OverlayGeometry? overlay)
    {
        lock (_overlayLock)
        {
            _overlay = overlay;
        }
    }

    public (double LatDeg, double LonDeg) ScreenToLatLon(int xPx, int yPx)
    {
        var cam = Camera.Snapshot();
        double mx = cam.CenterX + (xPx - cam.ViewportWidth / 2.0) * cam.MetersPerPixel;
        double my = cam.CenterY - (yPx - cam.ViewportHeight / 2.0) * cam.MetersPerPixel;
        return GeoMath.FromMercator(mx, my);
    }

    private readonly Lock _sweepLock = new();
    private (SweepGeometry Geometry, byte[] Palette, float Min, float Range)? _pendingSweep;
    private bool _sweepClearRequested;

    /// <summary>Stage a sweep for display; geometry prep runs on the calling thread.</summary>
    public void ShowSweep(Sweep sweep, ColorTable palette)
    {
        var rgba = palette.BuildRgba256();
        SetLegend(rgba, palette.MinValue, palette.MaxValue, palette.Name);
        ShowGeometry(SweepGeometry.Build(sweep), rgba, palette.MinValue, palette.Range);
    }

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

    private readonly TileFetcher _mosaicFetcher;
    private readonly TileFetcher _satelliteFetcher;
    private volatile bool _mosaicEnabled;
    private volatile float _mosaicOpacity = 0.75f;
    private volatile bool _satelliteEnabled;
    private volatile float _satelliteOpacity = 0.6f;

    /// <summary>GOES infrared under the radar layers.</summary>
    public bool SatelliteEnabled
    {
        get => _satelliteEnabled;
        set => _satelliteEnabled = value;
    }

    public float SatelliteOpacity
    {
        get => _satelliteOpacity;
        set => _satelliteOpacity = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Show the national NEXRAD mosaic under the single-site sweep — the seamless
    /// CONUS picture every consumer app opens with.
    /// </summary>
    public bool MosaicEnabled
    {
        get => _mosaicEnabled;
        set => _mosaicEnabled = value;
    }

    public float MosaicOpacity
    {
        get => _mosaicOpacity;
        set => _mosaicOpacity = Math.Clamp(value, 0f, 1f);
    }

    public MapView(TileProvider provider)
    {
        _fetcher = new TileFetcher(provider);
        _mosaicFetcher = new TileFetcher(TileProvider.NexradMosaic(provider.UserAgent));
        _satelliteFetcher = new TileFetcher(TileProvider.GoesInfrared(provider.UserAgent));
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
        _dragStartX = x;
        _dragStartY = y;
        _velX = _velY = 0;
        _lastMoveTicks = Stopwatch.GetTimestamp();
        Camera.StopInertia();
    }

    public void OnMouseMove(int x, int y)
    {
        if (!_dragging)
        {
            Hovered?.Invoke(x, y);
            return;
        }
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
        if (Math.Abs(_lastX - _dragStartX) + Math.Abs(_lastY - _dragStartY) < 5)
        {
            Clicked?.Invoke(_lastX, _lastY);
            return;
        }
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
        using var mosaicTextures = new TileTextureCache(device.Device, capacity: 600);
        using var satelliteTextures = new TileTextureCache(device.Device, capacity: 600);
        using var quads = new QuadRenderer(device.Device, device.Context);
        using var radar = new RadarSweepRenderer(device.Device, device.Context);
        using var overlay = new OverlayRenderer(device.Device, device.Context);

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

            // Upload any tiles the fetchers completed since last frame.
            while (_fetcher.TryDequeueCompleted(out var done))
                textures.Add(done.Key, done.Bgra);
            while (_mosaicFetcher.TryDequeueCompleted(out var mosaicDone))
                mosaicTextures.Add(mosaicDone.Key, mosaicDone.Bgra);
            while (_satelliteFetcher.TryDequeueCompleted(out var satelliteDone))
                satelliteTextures.Add(satelliteDone.Key, satelliteDone.Bgra);

            var cam = Camera.Snapshot();
            var ctx = device.Context;
            textures.BeginFrame();
            mosaicTextures.BeginFrame();
            satelliteTextures.BeginFrame();

            ctx.OMSetRenderTargets(device.BackBufferView!);
            ctx.RSSetViewport(0, 0, device.Width, device.Height);
            ctx.ClearRenderTargetView(device.BackBufferView!, clearColor);

            quads.Begin();
            DrawTiles(cam, textures, quads, _fetcher);
            // Satellite sits under the radar layers: cloud context, not the subject.
            if (_satelliteEnabled)
                DrawTiles(cam, satelliteTextures, quads, _satelliteFetcher, _satelliteOpacity, maxZoom: 10);
            // The mosaic is only published to zoom 12; above that we stretch its deepest tile.
            if (_mosaicEnabled)
                DrawTiles(cam, mosaicTextures, quads, _mosaicFetcher, _mosaicOpacity, maxZoom: 12);
            DrawImageOverlay(cam, quads, device);

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
            radar.Opacity = _radarOpacity;
            radar.Smoothing = _radarSmoothing;
            radar.Draw(cam);
            LastSweepUploadMs = radar.LastUploadMs;

            OverlayGeometry? overlayGeometry;
            lock (_overlayLock)
            {
                overlayGeometry = _overlay;
            }
            if (overlayGeometry is not null)
                overlay.Draw(overlayGeometry, cam); // warnings sit above radar by z-order

            quads.Begin();
            DrawMarkers(cam, quads);
            DrawLabels(cam, quads, device);
            DrawLegend(cam, quads, device);

            device.SwapChain.Present(1);
            ServiceCaptureRequest(device);

            fpsFrames++;
            if (now - fpsWindowStart >= Stopwatch.Frequency)
            {
                FramesPerSecond = fpsFrames / ((now - fpsWindowStart) / (double)Stopwatch.Frequency);
                fpsFrames = 0;
                fpsWindowStart = now;
            }
        }

        // The glyph texture belongs to this loop's device; drop it so a restarted
        // loop rebuilds against its own device.
        _glyphView?.Dispose();
        _glyphView = null;
        _glyphTexture?.Dispose();
        _glyphTexture = null;
        _legendView?.Dispose();
        _legendView = null;
        _legendTexture?.Dispose();
        _legendTexture = null;
        _uploadedLegend = null;
        _imageView?.Dispose();
        _imageView = null;
        _imageTexture?.Dispose();
        _imageTexture = null;
        _uploadedImage = null;
    }

    private void DrawTiles(
        CameraSnapshot cam, TileTextureCache textures, QuadRenderer quads,
        TileFetcher fetcher, float opacity = 1f, int maxZoom = 19)
    {
        int zoom = Math.Min(TileMath.ZoomForMetersPerPixel(cam.MetersPerPixel), maxZoom);
        var (minX, minY, maxX, maxY) = cam.WorldBounds();

        foreach (var key in TileMath.Cover(minX, minY, maxX, maxY, zoom))
        {
            var bounds = TileMath.Bounds(key);
            var clip = cam.ToClip(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);

            if (textures.TryGet(key, out var view))
            {
                quads.DrawTextured(clip, (0, 0, 1, 1), view, opacity);
                continue;
            }

            fetcher.Request(key);

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
                quads.DrawTextured(clip, (u0, v0, u0 + size, v0 + size), ancestorView, opacity);
                break;
            }
        }
    }

    private Vortice.Direct3D11.ID3D11Texture2D? _glyphTexture;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _glyphView;
    private Vortice.Direct3D11.ID3D11Texture2D? _legendTexture;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _legendView;

    private unsafe void DrawImageOverlay(CameraSnapshot cam, QuadRenderer quads, DeviceResources device)
    {
        var overlay = _imageOverlay;
        if (overlay is null)
        {
            if (_uploadedImage is not null)
            {
                _imageView?.Dispose(); _imageView = null;
                _imageTexture?.Dispose(); _imageTexture = null;
                _uploadedImage = null;
            }
            return;
        }

        if (!ReferenceEquals(_uploadedImage, overlay))
        {
            _imageView?.Dispose();
            _imageTexture?.Dispose();
            fixed (byte* p = overlay.Bgra)
            {
                var desc = new Vortice.Direct3D11.Texture2DDescription
                {
                    Width = (uint)overlay.Width,
                    Height = (uint)overlay.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                    SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                    Usage = Vortice.Direct3D11.ResourceUsage.Immutable,
                    BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource,
                };
                _imageTexture = device.Device.CreateTexture2D(desc,
                    [new Vortice.Direct3D11.SubresourceData((IntPtr)p, (uint)(overlay.Width * 4))]);
                _imageView = device.Device.CreateShaderResourceView(_imageTexture);
            }
            _uploadedImage = overlay;
        }

        var clip = cam.ToClip(overlay.MinX, overlay.MinY, overlay.MaxX, overlay.MaxY);
        quads.DrawTextured(clip, (0, 0, 1, 1), _imageView!, overlay.Opacity);
    }

    private unsafe void EnsureGlyphTexture(DeviceResources device)
    {
        if (_glyphView is not null) return;
        fixed (byte* p = _glyphAtlas.Bgra)
        {
            var desc = new Vortice.Direct3D11.Texture2DDescription
            {
                Width = (uint)_glyphAtlas.TextureWidth,
                Height = (uint)_glyphAtlas.TextureHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = Vortice.Direct3D11.ResourceUsage.Immutable,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource,
            };
            _glyphTexture = device.Device.CreateTexture2D(desc,
                [new Vortice.Direct3D11.SubresourceData((IntPtr)p, (uint)(_glyphAtlas.TextureWidth * 4))]);
            _glyphView = device.Device.CreateShaderResourceView(_glyphTexture);
        }
    }

    /// <summary>Draw text anchored in clip space, with a dark halo for legibility over any map.</summary>
    private void DrawTextClip(
        QuadRenderer quads, CameraSnapshot cam, float clipX, float clipY, string text,
        float glyphHeightPx = 15f, bool halo = true)
    {
        float glyphWidthPx = glyphHeightPx * GlyphAtlas.CellWidth / GlyphAtlas.CellHeight;
        float pxToClipX = 2f / cam.ViewportWidth;
        float pxToClipY = 2f / cam.ViewportHeight;

        for (int pass = halo ? 0 : 1; pass < 2; pass++)
        {
            float x = clipX + (pass == 0 ? 1.2f * pxToClipX : 0);
            float y = clipY + (pass == 0 ? -1.2f * pxToClipY : 0);
            float tint = pass == 0 ? 0f : 1f;
            foreach (var c in text)
            {
                if (_glyphAtlas.UvFor(c) is { } uv)
                {
                    quads.DrawTextured(
                        (x, y, x + glyphWidthPx * pxToClipX, y + glyphHeightPx * pxToClipY),
                        (uv.U0, uv.V0, uv.U1, uv.V1), _glyphView!,
                        pass == 0 ? 0.9f : 1f, tint, tint, tint);
                }
                x += glyphWidthPx * pxToClipX;
            }
        }
    }

    /// <summary>Viewport pixels (origin top-left) to clip space.</summary>
    private static (float X, float Y) PxToClip(CameraSnapshot cam, float xPx, float yPx) =>
        (xPx / cam.ViewportWidth * 2f - 1f, 1f - yPx / cam.ViewportHeight * 2f);

    private void DrawLabels(CameraSnapshot cam, QuadRenderer quads, DeviceResources device)
    {
        var labels = _labels;
        if (labels.Count == 0) return;
        EnsureGlyphTexture(device);

        float pxToClipX = 2f / cam.ViewportWidth;
        float pxToClipY = 2f / cam.ViewportHeight;
        double halfW = cam.ViewportWidth * cam.MetersPerPixel / 2.0;
        double halfH = cam.ViewportHeight * cam.MetersPerPixel / 2.0;

        foreach (var label in labels)
        {
            float anchorX = (float)((label.MercX - cam.CenterX) / halfW);
            float anchorY = (float)((label.MercY - cam.CenterY) / halfH);
            if (anchorX < -1.1f || anchorX > 1.1f || anchorY < -1.1f || anchorY > 1.1f)
                continue;
            DrawTextClip(quads, cam,
                anchorX + label.OffsetXPx * pxToClipX,
                anchorY + label.OffsetYPx * pxToClipY,
                label.Text);
        }
    }

    /// <summary>
    /// Colour scale for the displayed product: a vertical bar of the palette with labelled
    /// breakpoints, so a colour on the map can be read back as a number.
    /// </summary>
    private unsafe void DrawLegend(CameraSnapshot cam, QuadRenderer quads, DeviceResources device)
    {
        var legend = _legend;
        if (legend is null || legend.Max <= legend.Min) return;
        EnsureGlyphTexture(device);

        if (!ReferenceEquals(_uploadedLegend, legend))
        {
            // 1x256 texture so the palette runs vertically; row 0 is the high end.
            var column = new byte[256 * 4];
            for (int row = 0; row < 256; row++)
            {
                int source = (255 - row) * 4;
                Array.Copy(legend.Rgba, source, column, row * 4, 4);
            }
            _legendView?.Dispose();
            _legendTexture?.Dispose();
            fixed (byte* p = column)
            {
                var desc = new Vortice.Direct3D11.Texture2DDescription
                {
                    Width = 1,
                    Height = 256,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Vortice.DXGI.Format.R8G8B8A8_UNorm,
                    SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                    Usage = Vortice.Direct3D11.ResourceUsage.Immutable,
                    BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource,
                };
                _legendTexture = device.Device.CreateTexture2D(desc,
                    [new Vortice.Direct3D11.SubresourceData((IntPtr)p, 4)]);
                _legendView = device.Device.CreateShaderResourceView(_legendTexture);
            }
            _uploadedLegend = legend;
        }

        const float barX = 16f, barW = 20f, marginTop = 34f;
        float barH = Math.Min(300f, cam.ViewportHeight * 0.45f);
        float barTop = marginTop;
        float barBottom = barTop + barH;
        if (barH < 90f) return; // too short to label meaningfully

        // Backing plate so the scale stays readable over bright basemaps.
        var plateTl = PxToClip(cam, barX - 8f, barTop - 22f);
        var plateBr = PxToClip(cam, barX + barW + 62f, barBottom + 8f);
        quads.DrawSolid((plateTl.X, plateBr.Y, plateBr.X, plateTl.Y), 0.05f, 0.06f, 0.08f, 0.72f);

        var barTl = PxToClip(cam, barX, barTop);
        var barBr = PxToClip(cam, barX + barW, barBottom);
        quads.DrawTextured((barTl.X, barBr.Y, barBr.X, barTl.Y), (0, 0, 1, 1), _legendView!);

        var title = PxToClip(cam, barX - 4f, barTop - 19f);
        DrawTextClip(quads, cam, title.X, title.Y, legend.Title, 13f);

        float range = legend.Max - legend.Min;
        float step = NiceStep(range / 5f);
        float first = MathF.Ceiling(legend.Min / step) * step;
        for (float value = first; value <= legend.Max + 0.001f; value += step)
        {
            float t = (value - legend.Min) / range;
            float y = barBottom - t * barH;
            var tick = PxToClip(cam, barX + barW + 1f, y - 1.5f);
            quads.DrawSolid((tick.X, PxToClip(cam, 0, y + 1.5f).Y,
                PxToClip(cam, barX + barW + 6f, 0).X, tick.Y), 1f, 1f, 1f, 0.85f);
            var label = PxToClip(cam, barX + barW + 9f, y - 6f);
            DrawTextClip(quads, cam, label.X, label.Y, FormatTick(value, step), 12f);
        }
    }

    private static string FormatTick(float value, float step) =>
        step < 1f ? value.ToString("0.0") : value.ToString("0");

    /// <summary>Round a raw interval up to 1, 2, 2.5 or 5 times a power of ten.</summary>
    internal static float NiceStep(float raw)
    {
        if (raw <= 0) return 1f;
        float magnitude = MathF.Pow(10, MathF.Floor(MathF.Log10(raw)));
        float normalized = raw / magnitude;
        float nice = normalized <= 1f ? 1f : normalized <= 2f ? 2f : normalized <= 2.5f ? 2.5f : normalized <= 5f ? 5f : 10f;
        return nice * magnitude;
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
        _mosaicFetcher.Dispose();
        _satelliteFetcher.Dispose();
    }
}
