using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenWSR.Ingest;
using OpenWSR.Nexrad;
using OpenWSR.Render;
using OpenWSR.Render.Tiles;

namespace OpenWSR.App;

/// <summary>
/// Multi-pane (1/2/4) layout. Pane 0 is the primary — it keeps the overlays, live feed,
/// loop, and toolbar interactions.
///
/// A secondary pane does one of two things. By default it <b>follows</b> the primary:
/// same volume, different product, which makes the view a product comparison. Pin it to a
/// site instead and it fetches that site itself, which makes the view a place comparison —
/// two storms on two radars at once. Each pane says which it is doing in a header strip
/// above its surface; that strip is WPF sitting <em>above</em> the D3D child window rather
/// than over it, because nothing can be drawn on top of that rectangle.
///
/// Linked pan copies whichever camera moved last to the rest at 30 Hz.
/// </summary>
public sealed class PaneManager : IDisposable
{
    private sealed class Pane
    {
        public required MapView MapView { get; init; }
        public required RadarDisplayController Radar { get; init; }
        public required Border Host { get; init; }
        public ComboBox? SiteSelector { get; init; }
        public TextBlock? ProductLabel { get; init; }
        public PaneFeed? Feed { get; init; }

        /// <summary>
        /// Where this pane's camera was at the last link tick. Held on the pane rather
        /// than in an array indexed by position, because that position changes whenever
        /// panes are added, removed, or pinned — and a stale entry makes the link pick the
        /// wrong pane as the one that moved and yank every other pane to it.
        /// </summary>
        public CameraSnapshot LastSnapshot { get; set; }
    }

    /// <summary>The entry that means "show whatever the primary is showing".</summary>
    private const string FollowPrimary = "Follow primary";

    private readonly Grid _grid;
    private readonly TileProvider _provider;
    private readonly Pane _primary;
    private readonly List<Pane> _secondaries = [];
    private readonly DispatcherTimer _linkTimer;
    private RadarVolume? _currentVolume;

    // What the rest of the app is showing, so a newly pinned pane can catch up at once.
    private DataMode _mode = DataMode.Archive;
    private DateOnly _day = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateTime _targetUtc = DateTime.UtcNow;

    public bool LinkedPan { get; set; } = true;

    /// <summary>Raised on the UI thread with anything a pinned pane wants to say.</summary>
    public event Action<string>? StatusChanged;

    public PaneManager(Grid grid, MapView primaryView, RadarDisplayController primaryRadar,
        Border primaryHost, TileProvider provider)
    {
        _grid = grid;
        _provider = provider;
        _primary = new Pane { MapView = primaryView, Radar = primaryRadar, Host = primaryHost };

        _linkTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _linkTimer.Tick += (_, _) => SyncCameras();
        _linkTimer.Start();
    }

    /// <summary>Route the primary's volume to every pane that is following it.</summary>
    public void ShowVolume(RadarVolume volume)
    {
        _currentVolume = volume;
        _primary.Radar.ShowVolume(volume);
        foreach (var pane in _secondaries)
        {
            if (pane.Feed?.IsPinned != true) pane.Radar.ShowVolume(volume);
            UpdateProductLabel(pane);
        }
    }

    /// <summary>
    /// Tell the panes what the app is looking at now, so pinned ones can match it. Cheap
    /// and idempotent — a pane that is already on the right scan does nothing.
    /// </summary>
    public void NotifyContext(DataMode mode, DateOnly day, DateTime targetUtc)
    {
        _mode = mode;
        _day = day;
        _targetUtc = targetUtc;
        foreach (var pane in _secondaries)
            if (pane.Feed is { IsPinned: true } feed)
                _ = feed.SyncAsync(mode, day, targetUtc);
    }

    public void SetPaneCount(int count)
    {
        count = count switch { <= 1 => 1, 2 => 2, _ => 4 };
        int secondariesWanted = count - 1;

        while (_secondaries.Count > secondariesWanted)
        {
            var pane = _secondaries[^1];
            _secondaries.RemoveAt(_secondaries.Count - 1);
            _grid.Children.Remove(pane.Host);
            pane.Feed?.Dispose();
            DetachSurface(pane.Host);
            pane.MapView.Dispose();
        }
        while (_secondaries.Count < secondariesWanted)
            _secondaries.Add(CreateSecondary(_secondaries.Count));

        Relayout(count);
    }

    private float _filterMin = float.NegativeInfinity;
    private float _filterMax = float.PositiveInfinity;

    /// <summary>
    /// Apply the reflectivity window to every pane, not just the one that owns the slider.
    /// </summary>
    /// <remarks>
    /// Each pane has its own <see cref="MapView"/>, so a filter set on the primary reached
    /// nothing else. That was invisible while the window defaulted to everything and became
    /// visible the moment it defaulted to 20 dBZ: two panes on the same site and product
    /// drawing demonstrably different data. The decision is per pane because the window is
    /// reflectivity-only and panes deliberately open on different products.
    /// </remarks>
    public void ApplyValueFilter(float min, float max)
    {
        _filterMin = min;
        _filterMax = max;
        foreach (var pane in _secondaries) ApplyValueFilterTo(pane.MapView, pane.Radar);
    }

    private void ApplyValueFilterTo(MapView mapView, RadarDisplayController radar)
    {
        if (radar.CurrentMoment == Moment.Reflectivity)
            mapView.SetValueFilter(_filterMin, _filterMax);
        else
            mapView.SetValueFilter(float.NegativeInfinity, float.PositiveInfinity);
    }

    private float _radarOpacity = 0.85f;
    private float _radarSmoothing;

    /// <summary>
    /// Apply the radar opacity and smoothing sliders to every pane, not just the one that
    /// owns them.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="ApplyValueFilter"/> and there for the same reason: each
    /// pane has its own <see cref="MapView"/>, so a slider set on the primary reached nothing
    /// else. Smoothing hid it for as long as it shipped at 0, which is exactly what a freshly
    /// built secondary was already at, and it surfaced the moment the default became 50 —
    /// two panes on the same site and product, one smoothed and one raw, with the panel
    /// reporting a single number for both.
    /// </remarks>
    public void ApplyRadarAppearance(float opacity, float smoothing)
    {
        _radarOpacity = opacity;
        _radarSmoothing = smoothing;
        foreach (var pane in _secondaries)
        {
            pane.MapView.RadarOpacity = opacity;
            pane.MapView.RadarSmoothing = smoothing;
        }
    }

    private Pane CreateSecondary(int index)
    {
        var mapView = new MapView(_provider);
        var radar = new RadarDisplayController(mapView);

        // A pane opened later has to start where the sliders already are, not at the
        // renderer's own field defaults.
        mapView.RadarOpacity = _radarOpacity;
        mapView.RadarSmoothing = _radarSmoothing;

        // Secondary panes open on complementary products, so four panes are four views
        // rather than four copies.
        int defaultKey = index switch { 0 => 0x56, 1 => 0x44, _ => 0x43 }; // V, D, C
        radar.OnKey(defaultKey);
        mapView.KeyPressed += key =>
        {
            radar.OnKey(key);
            // A key may have changed this pane's product, and the window is reflectivity-only.
            ApplyValueFilterTo(mapView, radar);
        };

        var camera = _primary.MapView.Camera.Snapshot();
        mapView.Camera.SetView(camera.CenterX, camera.CenterY, camera.MetersPerPixel);

        ApplyValueFilterTo(mapView, radar);

        var feed = new PaneFeed();
        var pane = new Pane
        {
            MapView = mapView,
            Radar = radar,
            Host = new Border(),
            Feed = feed,
            SiteSelector = BuildSiteSelector(),
            ProductLabel = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 6, 0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextDim"],
            },
        };

        feed.VolumeReady += volume => _grid.Dispatcher.BeginInvoke(() =>
        {
            radar.ShowVolume(volume);
            UpdateProductLabel(pane);
        });
        feed.StatusChanged += text => _grid.Dispatcher.BeginInvoke(() => StatusChanged?.Invoke(text));
        radar.SelectionChanged += () => _grid.Dispatcher.BeginInvoke(() => UpdateProductLabel(pane));

        pane.SiteSelector!.SelectionChanged += async (_, _) =>
        {
            string? site = pane.SiteSelector.SelectedItem as string;
            await feed.SetSiteAsync(site == FollowPrimary ? null : site);

            if (feed.IsPinned)
            {
                // Pinning means "show me that place", so go there. A pinned pane is also
                // dropped from camera linking below — otherwise the link would drag it
                // straight back and you would be looking at the right data over the wrong
                // ground.
                if (RadarSites.ByIcao(feed.Site!) is { } target)
                    mapView.Camera.MoveTo(target.LatDeg, target.LonDeg, 250);
                await feed.SyncAsync(_mode, _day, _targetUtc);
            }
            else
            {
                // Back to following: rejoin the others where they are.
                var shared = _primary.MapView.Camera.Snapshot();
                mapView.Camera.SetView(shared.CenterX, shared.CenterY, shared.MetersPerPixel);
                if (_currentVolume is not null) radar.ShowVolume(_currentVolume);
            }
            UpdateProductLabel(pane);
        };

        // Header above the surface, never over it: the D3D child window owns its rectangle.
        var header = new Border
        {
            Background = (Brush)Application.Current.Resources["Rail"],
            BorderBrush = (Brush)Application.Current.Resources["Line"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 2, 4, 2),
            Child = new DockPanel { Children = { pane.SiteSelector, pane.ProductLabel! } },
        };
        DockPanel.SetDock(header, Dock.Top);

        var layout = new DockPanel();
        layout.Children.Add(header);
        layout.Children.Add(new D3DHostControl(mapView));
        pane.Host.Child = layout;

        _grid.Children.Add(pane.Host);
        if (_currentVolume is not null) radar.ShowVolume(_currentVolume);
        UpdateProductLabel(pane);
        return pane;
    }

    private static ComboBox BuildSiteSelector()
    {
        var combo = new ComboBox
        {
            Width = 190,
            Height = 22,
            FontSize = 11,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Show another radar in this pane, or follow the main one",
        };
        System.Windows.Automation.AutomationProperties.SetName(combo, "Pane radar site");
        combo.Items.Add(FollowPrimary);
        foreach (var site in RadarSites.All.Where(s => !s.IsTdwr).OrderBy(s => s.Icao))
            combo.Items.Add(site.Icao);
        combo.SelectedIndex = 0;
        return combo;
    }

    private static void UpdateProductLabel(Pane pane)
    {
        if (pane.ProductLabel is null) return;
        var sweep = pane.Radar.DisplayedSweep;
        pane.ProductLabel.Text = sweep is null
            ? $"{pane.Radar.CurrentMoment} — no data"
            : $"{sweep.SiteId}  {pane.Radar.CurrentMoment}  {sweep.ElevationAngleDeg:F1}°  {sweep.ScanTimeUtc:HH:mm}Z";
    }

    /// <summary>Tear down the D3D child so its render thread stops before the view is disposed.</summary>
    private static void DetachSurface(Border host)
    {
        if (host.Child is DockPanel panel)
        {
            foreach (var child in panel.Children.OfType<D3DHostControl>().ToList())
                panel.Children.Remove(child);
        }
        host.Child = null;
    }

    private void Relayout(int count)
    {
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        int columns = count >= 2 ? 2 : 1;
        int rows = count >= 4 ? 2 : 1;
        for (int i = 0; i < columns; i++)
            _grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows; i++)
            _grid.RowDefinitions.Add(new RowDefinition());

        var hosts = new List<Border> { _primary.Host };
        hosts.AddRange(_secondaries.Select(p => p.Host));
        for (int i = 0; i < hosts.Count; i++)
        {
            Grid.SetColumn(hosts[i], i % columns);
            Grid.SetRow(hosts[i], i / columns);
            hosts[i].Margin = count > 1 ? new Thickness(1) : default;
        }
    }

    private void SyncCameras()
    {
        if (!LinkedPan || _secondaries.Count == 0) return;

        // A pinned pane is looking at its own site, so it neither drives the link nor
        // follows it.
        var panes = new List<Pane> { _primary };
        panes.AddRange(_secondaries.Where(p => p.Feed?.IsPinned != true));
        if (panes.Count < 2) return;

        // Whichever camera changed since last tick becomes the source of truth.
        Pane? moved = null;
        foreach (var pane in panes)
        {
            var snapshot = pane.MapView.Camera.Snapshot();
            if (snapshot.CenterX != pane.LastSnapshot.CenterX ||
                snapshot.CenterY != pane.LastSnapshot.CenterY ||
                snapshot.MetersPerPixel != pane.LastSnapshot.MetersPerPixel)
            {
                moved = pane;
                break;
            }
        }
        if (moved is not null)
        {
            var source = moved.MapView.Camera.Snapshot();
            foreach (var pane in panes)
                if (!ReferenceEquals(pane, moved))
                    pane.MapView.Camera.SetView(source.CenterX, source.CenterY, source.MetersPerPixel);
        }
        foreach (var pane in panes)
            pane.LastSnapshot = pane.MapView.Camera.Snapshot();
    }

    public void Dispose()
    {
        _linkTimer.Stop();
        foreach (var pane in _secondaries)
        {
            pane.Feed?.Dispose();
            DetachSurface(pane.Host);
            pane.MapView.Dispose();
        }
        _secondaries.Clear();
    }
}
