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
    private readonly List<(double MercX, double MercY, string Label)> _markers = [];
    private (double MercX, double MercY)? _selectedMarker;
    private volatile bool _markersEnabled = true;

    /// <summary>
    /// Above this scale the site layer hides. 210 markers at national zoom is a red rash
    /// over the mosaic that the national view exists to show.
    /// </summary>
    private const double MarkerHideAboveMpp = 2200;

    /// <summary>Below this scale each site gets its ICAO drawn beside it.</summary>
    private const double MarkerLabelBelowMpp = 400;

    /// <summary>Show the WSR-88D site layer.</summary>
    public bool MarkersEnabled
    {
        get => _markersEnabled;
        set => _markersEnabled = value;
    }

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

    private volatile float _filterMin = float.NegativeInfinity;
    private volatile float _filterMax = float.PositiveInfinity;

    /// <summary>
    /// Draw only gates whose value falls inside this window, in the palette's own units.
    /// </summary>
    /// <remarks>
    /// Read per frame rather than applied to the data, so dragging the control redraws the
    /// sweep already on screen — no decode, no restage, and archive playback keeps its
    /// prebuilt geometry.
    /// </remarks>
    public void SetValueFilter(float min, float max)
    {
        _filterMin = min;
        _filterMax = max;
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
    private int _rightLastX, _rightLastY;

    /// <summary>Viewport height as of the last frame; a pan has to scale by it.</summary>
    private volatile int _lastViewportHeight = 1;

    public void OnRightDown(int x, int y)
    {
        _rightDragging = true;
        _rightStartX = _rightLastX = x;
        _rightStartY = _rightLastY = y;
    }

    public void OnRightMove(int x, int y)
    {
        if (!_rightDragging) return;

        if (_volumeMode)
        {
            // Incremental, so the pan follows the hand rather than accelerating away from
            // the point the drag started.
            VolumeCamera.Pan(x - _rightLastX, y - _rightLastY, _lastViewportHeight);
            _rightLastX = x;
            _rightLastY = y;
            return;
        }

        MeasureDragged?.Invoke(_rightStartX, _rightStartY, x, y, false);
    }

    public void OnRightUp(int x, int y)
    {
        if (!_rightDragging) return;
        _rightDragging = false;
        if (_volumeMode) return;
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

    /// <summary>
    /// A decoded icon sheet: one image holding a grid of icons. Supplied already decoded
    /// because imaging belongs to the app layer — this assembly has no PNG decoder and
    /// should not grow one.
    /// </summary>
    public sealed record IconSheet(byte[] Bgra, int Width, int Height, int CellWidth, int CellHeight);

    /// <summary>One icon to draw: which sheet, which cell, where, and turned how far.</summary>
    public readonly record struct MapIcon(
        double MercX, double MercY, int Sheet, int Cell,
        double AngleDeg, double HotXFraction, double HotYFraction);

    private IReadOnlyDictionary<int, IconSheet> _iconSheets =
        new Dictionary<int, IconSheet>();
    private IReadOnlyList<MapIcon> _icons = [];
    private readonly Dictionary<int, (Vortice.Direct3D11.ID3D11Texture2D Texture,
        Vortice.Direct3D11.ID3D11ShaderResourceView View, IconSheet Source)> _iconTextures = [];

    /// <summary>
    /// Replace the icon layer and the sheets it draws from. Both are swapped together
    /// because an icon indexes into a sheet, and a half-updated pair draws the wrong
    /// picture rather than none.
    /// </summary>
    public void SetIcons(IReadOnlyDictionary<int, IconSheet> sheets, IReadOnlyList<MapIcon> icons)
    {
        _iconSheets = sheets;
        _icons = icons;
    }

    /// <summary>A pre-rendered BGRA image pinned to a Mercator rectangle.</summary>
    public sealed record ImageOverlay(
        byte[] Bgra, int Width, int Height,
        double MinX, double MinY, double MaxX, double MaxY, float Opacity);

    /// <summary>
    /// Which pass a georeferenced raster is drawn in. The distinction is z-order: a field
    /// like model output stands in for the radar and belongs under it, while an analysis
    /// product is read against the radar and belongs over it.
    /// </summary>
    public enum OverlaySlot
    {
        /// <summary>Under the radar sweep — model fields, mosaics.</summary>
        Field = 0,

        /// <summary>Over the radar sweep — derived products read against the echo.</summary>
        Analysis = 1,

        /// <summary>
        /// Over the radar too, but a slot of its own so it cannot fight
        /// <see cref="Analysis"/>.
        ///
        /// Rotation tracks and the hail swath are both accumulations over time drawn above
        /// the sweep, and sharing one slot meant each silently erased the other: switching
        /// the hail layer off cleared the slot, taking a rotation-track swath with it and
        /// not giving it back until the tracks were rebuilt from scratch. They are different
        /// quantities measuring different things, so unlike Field's claimants they are not
        /// alternatives and must not be made mutually exclusive.
        /// </summary>
        Swath = 3,

        /// <summary>
        /// Below everything else: cloud imagery, drawn where the satellite tiles are.
        ///
        /// Separate from <see cref="Field"/> on purpose. Field's claimants — the forecast
        /// raster, the MRMS composite — are the same quantity as the radar and are mutually
        /// exclusive with each other by construction. Clouds are a different measurement
        /// entirely and the whole point is to see them *under* precipitation, so sharing a
        /// slot would make the two turn each other off.
        /// </summary>
        Satellite = 2,
    }

    private const int OverlaySlotCount = 4;
    private readonly ImageOverlay?[] _imageOverlays = new ImageOverlay?[OverlaySlotCount];
    private readonly ImageOverlay?[] _uploadedImages = new ImageOverlay?[OverlaySlotCount];
    private readonly Vortice.Direct3D11.ID3D11Texture2D?[] _imageTextures =
        new Vortice.Direct3D11.ID3D11Texture2D?[OverlaySlotCount];
    private readonly Vortice.Direct3D11.ID3D11ShaderResourceView?[] _imageViews =
        new Vortice.Direct3D11.ID3D11ShaderResourceView?[OverlaySlotCount];

    /// <summary>
    /// Show a georeferenced raster over the basemap — model output or any gridded field
    /// already resampled into Web Mercator. Null clears it.
    /// </summary>
    public void SetImageOverlay(ImageOverlay? overlay) =>
        SetImageOverlay(OverlaySlot.Field, overlay);

    /// <summary>Show a georeferenced raster in one of the two z-order slots. Null clears it.</summary>
    public void SetImageOverlay(OverlaySlot slot, ImageOverlay? overlay) =>
        _imageOverlays[(int)slot] = overlay;

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

    // ---- 3D volume ----

    /// <summary>
    /// The orbit camera for the 3D view. Held here rather than inside the renderer because
    /// input arrives on the UI thread and the renderer belongs to the render thread; it
    /// keeps its pose in an immutable record swapped in one reference write, which is what
    /// makes reading it from the render thread safe.
    /// </summary>
    public VolumeCamera VolumeCamera { get; } = new();

    private volatile bool _volumeMode;

    /// <summary>
    /// When set, the map is replaced by the volume view. The two share a window and an
    /// input path, but nothing else: a 3D view of one radar is not a layer over a map of
    /// the country, and pretending otherwise would mean a camera that means two things.
    /// </summary>
    public bool VolumeMode
    {
        get => _volumeMode;
        set => _volumeMode = value;
    }

    private readonly Lock _volumeLock = new();
    private VolumeUpload? _pendingVolume;
    private bool _volumeClearRequested;

    /// <summary>Stage a resampled volume; the render thread uploads it next frame.</summary>
    public void SetVolume(VolumeUpload upload)
    {
        lock (_volumeLock)
        {
            _pendingVolume = upload;
            _volumeClearRequested = false;
        }
    }

    public void ClearVolume()
    {
        lock (_volumeLock)
        {
            _pendingVolume = null;
            _volumeClearRequested = true;
        }
    }

    /// <summary>Vertical stretch of the 3D view; 1 is true scale. Read on the render thread.</summary>
    public float VolumeExaggeration { get; set; } = 6f;

    /// <summary>Display threshold for the 3D view, in the moment's own units.</summary>
    public float VolumeThreshold { get; set; } = 15f;

    /// <summary>How readily a voxel accumulates opacity: a thinner or waxier storm.</summary>
    public float VolumeDensity { get; set; } = 0.30f;

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

        // Only the dark style splits its labels out. The others bake them in, and drawing a
        // second copy over the radar would double every name.
        if (provider.Labels is { } labels)
        {
            _labelFetcher = new TileFetcher(labels);
            _labelBoost = provider.LabelBoost;
        }
    }

    private readonly TileFetcher? _labelFetcher;

    /// <summary>
    /// Set once the basemap looks genuinely broken rather than merely unlucky, and null
    /// otherwise. A broken tile source is indistinguishable on screen from a slow one, since
    /// the map is empty either way.
    /// </summary>
    /// <remarks>
    /// A threshold rather than the first failure. One 404, a rate limit, a dropped packet or
    /// a timeout is ordinary, and ArcGIS caches in particular answer 404 for tiles they
    /// simply do not hold at some zooms — the terrain basemap would raise a persistent error
    /// bar on a perfectly normal session. Twelve failures is more than a screen's worth of
    /// tiles and is not luck.
    /// </remarks>
    public string? BasemapFailure =>
        _fetcher.FailureCount >= 12 ? _fetcher.FirstFailure : null;

    /// <summary>
    /// How hard the place names are lifted. CARTO draws them mid-grey, which is dim against
    /// the near-black ground and invisible over a bright echo; 2.2 takes the brightest pixel
    /// of a label from 161 to white and leaves the anti-aliased edges as a soft falloff
    /// rather than a hard outline.
    /// </summary>
    private readonly float _labelBoost = 1f;

    /// <summary>
    /// Physical pixels per device-independent unit, from the display this window is on.
    /// Overlay stroke widths are quoted in DIUs and scaled by this, so a 3 pt line is 3 pt
    /// whether the screen is at 100 % or 200 %.
    /// </summary>
    public double DipScale { get; private set; } = 1.0;

    public void Start(IntPtr hwnd, int width, int height, double dipScale = 1.0)
    {
        _hwnd = hwnd;
        DipScale = dipScale;
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

    public void SetMarkers(IEnumerable<(double LatDeg, double LonDeg, string Label)> sites)
    {
        lock (_markerLock)
        {
            _markers.Clear();
            foreach (var (lat, lon, label) in sites)
            {
                var (x, y) = GeoMath.ToMercator(lat, lon);
                _markers.Add((x, y, label));
            }
        }
    }

    /// <summary>Mark one site as the selected one, so it reads differently from its neighbours.</summary>
    public void SetSelectedMarker(double? latDeg, double? lonDeg)
    {
        lock (_markerLock)
        {
            _selectedMarker = latDeg is { } lat && lonDeg is { } lon
                ? GeoMath.ToMercator(lat, lon)
                : null;
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
            // Nothing to read out of a 3D view under the cursor: the ray passes through
            // the whole storm, so there is no one value to report.
            if (!_volumeMode) Hovered?.Invoke(x, y);
            return;
        }
        int dx = x - _lastX, dy = y - _lastY;
        _lastX = x;
        _lastY = y;

        if (_volumeMode)
        {
            VolumeCamera.Orbit(dx, dy);
            return;
        }

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
        if (_volumeMode) return;
        if (Math.Abs(_lastX - _dragStartX) + Math.Abs(_lastY - _dragStartY) < 5)
        {
            Clicked?.Invoke(_lastX, _lastY);
            return;
        }
        double sinceMove = (Stopwatch.GetTimestamp() - _lastMoveTicks) / (double)Stopwatch.Frequency;
        if (sinceMove < 0.15)
            Camera.SetInertia(_velX, _velY);
    }

    public void OnMouseWheel(int clientX, int clientY, int delta)
    {
        if (_volumeMode)
        {
            VolumeCamera.Zoom(delta / 120.0);
            return;
        }
        Camera.ZoomAt(clientX, clientY, Math.Pow(1.25, delta / 120.0));
    }

    // ---- Render thread ----

    private void RenderLoop()
    {
        using var device = new DeviceResources(_hwnd, _pendingWidth, _pendingHeight);
        using var textures = new TileTextureCache(device.Device);
        using var mosaicTextures = new TileTextureCache(device.Device, capacity: 600);
        using var satelliteTextures = new TileTextureCache(device.Device, capacity: 600);
        using var labelTextures = new TileTextureCache(device.Device, capacity: 600);
        using var quads = new QuadRenderer(device.Device, device.Context);
        using var radar = new RadarSweepRenderer(device.Device, device.Context);
        using var overlay = new OverlayRenderer(device.Device, device.Context);
        using var volume = new VolumeRenderer(device.Device, device.Context);

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
            while (_labelFetcher is not null && _labelFetcher.TryDequeueCompleted(out var labelDone))
                labelTextures.Add(labelDone.Key, labelDone.Bgra);

            var cam = Camera.Snapshot();
            var ctx = device.Context;
            _lastViewportHeight = device.Height;
            textures.BeginFrame();
            mosaicTextures.BeginFrame();
            satelliteTextures.BeginFrame();
            labelTextures.BeginFrame();

            ctx.OMSetRenderTargets(device.BackBufferView!);
            ctx.RSSetViewport(0, 0, device.Width, device.Height);
            ctx.ClearRenderTargetView(device.BackBufferView!, clearColor);

            lock (_volumeLock)
            {
                if (_pendingVolume is not null)
                {
                    volume.SetVolume(_pendingVolume);
                    _pendingVolume = null;
                }
                else if (_volumeClearRequested)
                {
                    // Not volume.Clear(): this is the render thread, and the release has to
                    // happen here. Staging it would leave it for a Draw that no longer runs.
                    volume.ClearNow();
                    _volumeClearRequested = false;
                }
            }

            if (_volumeMode)
            {
                volume.VerticalExaggeration = VolumeExaggeration;
                volume.ThresholdValue = VolumeThreshold;
                volume.Density = VolumeDensity;
                volume.Draw(
                    VolumeCamera.Snapshot(device.Height > 0 ? device.Width / (float)device.Height : 1f),
                    device.Height);

                // The colour scale still applies, and it is drawn in screen space, so it
                // costs nothing to keep. Everything else on the map does not apply.
                quads.Begin();
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
                continue;
            }

            quads.Begin();
            DrawTiles(cam, textures, quads, _fetcher);
            // Cloud, under the radar layers: context, not the subject.
            //
            // Native ABI when it has loaded, and the pre-rendered tiles only when it has not.
            // "Only when" is the whole point — the tiles draw *after* the raster and would
            // otherwise cover the better data with the worse, which is not what a fallback
            // means. Both are the same field, so drawing both is never right.
            DrawImageOverlay(OverlaySlot.Satellite, cam, quads, device);
            if (_satelliteEnabled && _imageOverlays[(int)OverlaySlot.Satellite] is null)
            {
                // Capped at the sensor's real resolution rather than at what the server will
                // answer. ABI band 13 is 2 km at nadir and worse at CONUS latitudes, which is
                // about z6 in Mercator; ask for z10 and IEM does not refuse, it
                // nearest-neighbour upsamples, and every source pixel arrives as a hard-edged
                // block. That was the jagged look. Requesting z7 and letting the GPU's linear
                // filter magnify gives the smooth result, and is also the honest one — the
                // detail past here was never measured.
                DrawTiles(cam, satelliteTextures, quads, _satelliteFetcher, _satelliteOpacity, maxZoom: 7);
            }
            // The mosaic is only published to zoom 12; above that we stretch its deepest tile.
            if (_mosaicEnabled)
                DrawTiles(cam, mosaicTextures, quads, _mosaicFetcher, _mosaicOpacity, maxZoom: 12);
            DrawImageOverlay(OverlaySlot.Field, cam, quads, device);

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
            radar.FilterMin = _filterMin;
            radar.FilterMax = _filterMax;
            radar.Draw(cam);
            LastSweepUploadMs = radar.LastUploadMs;

            // Analysis rasters sit above the sweep: they are read against the echo, not
            // instead of it.
            quads.Begin();
            DrawImageOverlay(OverlaySlot.Analysis, cam, quads, device);
            DrawImageOverlay(OverlaySlot.Swath, cam, quads, device);

            // Place names last of the map layers, so they sit over the weather rather than
            // under it. The name of the town a storm is on top of is exactly what wants
            // reading at that moment, and baked into the basemap it is the first thing an
            // echo covers. Boosted, because the style draws them mid-grey.
            if (_labelFetcher is not null)
                DrawTiles(cam, labelTextures, quads, _labelFetcher, boost: _labelBoost);

            OverlayGeometry? overlayGeometry;
            lock (_overlayLock)
            {
                overlayGeometry = _overlay;
            }
            if (overlayGeometry is not null)
            {
                overlay.DipScale = (float)DipScale;
                overlay.Draw(overlayGeometry, cam); // warnings sit above radar by z-order
            }

            quads.Begin();
            DrawMarkers(cam, quads, device);
            DrawIcons(cam, quads, device);
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
        foreach (var (texture, view, _) in _iconTextures.Values)
        {
            view.Dispose();
            texture.Dispose();
        }
        _iconTextures.Clear();
        _legendView?.Dispose();
        _legendView = null;
        _legendTexture?.Dispose();
        _legendTexture = null;
        _uploadedLegend = null;
        for (int slot = 0; slot < OverlaySlotCount; slot++)
        {
            _imageViews[slot]?.Dispose();
            _imageViews[slot] = null;
            _imageTextures[slot]?.Dispose();
            _imageTextures[slot] = null;
            _uploadedImages[slot] = null;
        }
    }

    private void DrawTiles(
        CameraSnapshot cam, TileTextureCache textures, QuadRenderer quads,
        TileFetcher fetcher, float opacity = 1f, int maxZoom = 19, float boost = 1f)
    {
        int zoom = Math.Min(TileMath.ZoomForMetersPerPixel(cam.MetersPerPixel), maxZoom);
        var (minX, minY, maxX, maxY) = cam.WorldBounds();

        foreach (var key in TileMath.Cover(minX, minY, maxX, maxY, zoom))
        {
            var bounds = TileMath.Bounds(key);
            var clip = cam.ToClip(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);

            if (textures.TryGet(key, out var view))
            {
                quads.DrawTextured(clip, (0, 0, 1, 1), view, opacity, boost, boost, boost);
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
                quads.DrawTextured(
                    clip, (u0, v0, u0 + size, v0 + size), ancestorView, opacity, boost, boost, boost);
                break;
            }
        }
    }

    private Vortice.Direct3D11.ID3D11Texture2D? _glyphTexture;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _glyphView;
    private Vortice.Direct3D11.ID3D11Texture2D? _legendTexture;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _legendView;

    private unsafe void DrawImageOverlay(
        OverlaySlot slot, CameraSnapshot cam, QuadRenderer quads, DeviceResources device)
    {
        int i = (int)slot;
        var overlay = _imageOverlays[i];
        if (overlay is null)
        {
            if (_uploadedImages[i] is not null)
            {
                _imageViews[i]?.Dispose(); _imageViews[i] = null;
                _imageTextures[i]?.Dispose(); _imageTextures[i] = null;
                _uploadedImages[i] = null;
            }
            return;
        }

        if (!ReferenceEquals(_uploadedImages[i], overlay))
        {
            _imageViews[i]?.Dispose();
            _imageTextures[i]?.Dispose();
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
                _imageTextures[i] = device.Device.CreateTexture2D(desc,
                    [new Vortice.Direct3D11.SubresourceData((IntPtr)p, (uint)(overlay.Width * 4))]);
                _imageViews[i] = device.Device.CreateShaderResourceView(_imageTextures[i]);
            }
            _uploadedImages[i] = overlay;
        }

        var clip = cam.ToClip(overlay.MinX, overlay.MinY, overlay.MaxX, overlay.MaxY);
        quads.DrawTextured(clip, (0, 0, 1, 1), _imageViews[i]!, overlay.Opacity);
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

    /// <summary>
    /// Draw the placefile icon layer.
    ///
    /// Icons are a fixed size on screen and can be rotated, which the wedge-and-line
    /// overlay path cannot express — so they get their own pass, textured from the sheet
    /// the placefile named. The hot spot is what sits on the coordinate: a pin's tip
    /// rather than its middle.
    /// </summary>
    private unsafe void DrawIcons(CameraSnapshot cam, QuadRenderer quads, DeviceResources device)
    {
        var icons = _icons;
        var sheets = _iconSheets;
        if (icons.Count == 0 || sheets.Count == 0) return;

        // Drop textures for sheets that are gone or have been replaced.
        foreach (int id in _iconTextures.Keys.ToList())
        {
            if (sheets.TryGetValue(id, out var current) &&
                ReferenceEquals(_iconTextures[id].Source, current)) continue;
            var (texture, view, _) = _iconTextures[id];
            view.Dispose();
            texture.Dispose();
            _iconTextures.Remove(id);
        }

        double halfW = cam.ViewportWidth * cam.MetersPerPixel / 2.0;
        double halfH = cam.ViewportHeight * cam.MetersPerPixel / 2.0;
        float pxToClipX = 2f / cam.ViewportWidth;
        float pxToClipY = 2f / cam.ViewportHeight;

        foreach (var icon in icons)
        {
            if (!sheets.TryGetValue(icon.Sheet, out var sheet)) continue;

            float anchorX = (float)((icon.MercX - cam.CenterX) / halfW);
            float anchorY = (float)((icon.MercY - cam.CenterY) / halfH);
            if (anchorX < -1.2f || anchorX > 1.2f || anchorY < -1.2f || anchorY > 1.2f) continue;

            if (!_iconTextures.TryGetValue(icon.Sheet, out var resource))
            {
                fixed (byte* p = sheet.Bgra)
                {
                    var desc = new Vortice.Direct3D11.Texture2DDescription
                    {
                        Width = (uint)sheet.Width,
                        Height = (uint)sheet.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = Vortice.Direct3D11.ResourceUsage.Immutable,
                        BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource,
                    };
                    var texture = device.Device.CreateTexture2D(desc,
                        [new Vortice.Direct3D11.SubresourceData((IntPtr)p, (uint)(sheet.Width * 4))]);
                    resource = (texture, device.Device.CreateShaderResourceView(texture), sheet);
                }
                _iconTextures[icon.Sheet] = resource;
            }

            // Cells run left to right, then top to bottom, numbered from one.
            int columns = Math.Max(1, sheet.Width / sheet.CellWidth);
            int rows = Math.Max(1, sheet.Height / sheet.CellHeight);
            int index = Math.Clamp(icon.Cell - 1, 0, columns * rows - 1);
            int cellX = index % columns, cellY = index / columns;

            float u0 = (float)(cellX * sheet.CellWidth) / sheet.Width;
            float v0 = (float)(cellY * sheet.CellHeight) / sheet.Height;
            float u1 = (float)((cellX + 1) * sheet.CellWidth) / sheet.Width;
            float v1 = (float)((cellY + 1) * sheet.CellHeight) / sheet.Height;

            float width = sheet.CellWidth * pxToClipX;
            float height = sheet.CellHeight * pxToClipY;
            float left = -(float)icon.HotXFraction * width;
            float top = (float)icon.HotYFraction * height;

            quads.DrawTexturedRotated(
                anchorX, anchorY, left, top, width, height,
                (u0, v0, u1, v1), resource.View, (float)(icon.AngleDeg * Math.PI / 180.0),
                cam.ViewportWidth / (float)cam.ViewportHeight);
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

    /// <summary>
    /// Enough decimals to tell one tick from the next. A fixed single decimal labelled
    /// every tick of the azimuthal-shear scale "0.0", because its whole range is 0.04.
    /// </summary>
    private static string FormatTick(float value, float step) =>
        step >= 1f ? value.ToString("0")
        : step >= 0.1f ? value.ToString("0.0")
        : step >= 0.01f ? value.ToString("0.00")
        : step >= 0.001f ? value.ToString("0.000")
        : value.ToString("0.0000");

    /// <summary>Round a raw interval up to 1, 2, 2.5 or 5 times a power of ten.</summary>
    internal static float NiceStep(float raw)
    {
        if (raw <= 0) return 1f;
        float magnitude = MathF.Pow(10, MathF.Floor(MathF.Log10(raw)));
        float normalized = raw / magnitude;
        float nice = normalized <= 1f ? 1f : normalized <= 2f ? 2f : normalized <= 2.5f ? 2.5f : normalized <= 5f ? 5f : 10f;
        return nice * magnitude;
    }

    /// <summary>
    /// The WSR-88D site layer. It began as a Phase 3 alignment check and behaved like one:
    /// every site, every zoom, no labels. Now it hides at national scale, labels itself up
    /// close, and marks the selected site so you can see which one you are looking at.
    /// </summary>
    private void DrawMarkers(CameraSnapshot cam, QuadRenderer quads, DeviceResources device)
    {
        if (!_markersEnabled || cam.MetersPerPixel > MarkerHideAboveMpp) return;

        (double X, double Y, string Label)[] markers;
        (double X, double Y)? selected;
        lock (_markerLock)
        {
            markers = [.. _markers];
            selected = _selectedMarker;
        }
        if (markers.Length == 0) return;

        bool labelled = cam.MetersPerPixel < MarkerLabelBelowMpp;
        if (labelled) EnsureGlyphTexture(device);

        double half = 4 * cam.MetersPerPixel; // 8 px squares
        float pxToClipX = 2f / cam.ViewportWidth;
        float pxToClipY = 2f / cam.ViewportHeight;

        foreach (var (mx, my, label) in markers)
        {
            var clip = cam.ToClip(mx - half, my - half, mx + half, my + half);
            if (clip.X1 < -1 || clip.X0 > 1 || clip.Y1 < -1 || clip.Y0 > 1) continue;

            bool isSelected = selected is { } s &&
                              Math.Abs(s.X - mx) < 1 && Math.Abs(s.Y - my) < 1;
            if (isSelected)
            {
                // A larger amber square with a halo — the one site whose data is on screen.
                double outer = 7 * cam.MetersPerPixel;
                quads.DrawSolid(cam.ToClip(mx - outer, my - outer, mx + outer, my + outer),
                    1f, 1f, 1f, 0.55f);
                quads.DrawSolid(clip, 1f, 0.78f, 0.16f, 1f);
            }
            else
            {
                quads.DrawSolid(clip, 0.95f, 0.15f, 0.15f, 0.9f);
            }

            if (labelled && label.Length > 0)
                DrawTextClip(quads, cam,
                    clip.X1 + 4 * pxToClipX, clip.Y1 - 3 * pxToClipY, label, 12f);
        }
    }

    public void Dispose()
    {
        Stop();
        _fetcher.Dispose();
        _mosaicFetcher.Dispose();
        _satelliteFetcher.Dispose();
        _labelFetcher?.Dispose();
    }
}
