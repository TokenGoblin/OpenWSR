using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenWSR.Nexrad;
using OpenWSR.Render;
using OpenWSR.Render.Tiles;

namespace OpenWSR.App;

/// <summary>
/// Multi-pane (1/2/4) layout. Pane 0 is the primary — it keeps the overlays, live
/// feed, loop, and toolbar interactions. Secondary panes carry their own MapView and
/// product selection (they default to Velocity / ZDR / RhoHV) and receive every
/// volume the primary shows. Linked pan copies whichever camera moved last to the
/// rest at 30 Hz.
/// </summary>
public sealed class PaneManager : IDisposable
{
    private sealed record Pane(MapView MapView, RadarDisplayController Radar, Border Host);

    private readonly Grid _grid;
    private readonly TileProvider _provider;
    private readonly Pane _primary;
    private readonly List<Pane> _secondaries = [];
    private readonly DispatcherTimer _linkTimer;
    private readonly CameraSnapshot[] _lastSnapshots = new CameraSnapshot[4];
    private RadarVolume? _currentVolume;

    public bool LinkedPan { get; set; } = true;

    public PaneManager(Grid grid, MapView primaryView, RadarDisplayController primaryRadar,
        Border primaryHost, TileProvider provider)
    {
        _grid = grid;
        _provider = provider;
        _primary = new Pane(primaryView, primaryRadar, primaryHost);

        _linkTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _linkTimer.Tick += (_, _) => SyncCameras();
        _linkTimer.Start();
    }

    /// <summary>Route a volume to every pane (primary included).</summary>
    public void ShowVolume(RadarVolume volume)
    {
        _currentVolume = volume;
        _primary.Radar.ShowVolume(volume);
        foreach (var pane in _secondaries)
            pane.Radar.ShowVolume(volume);
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
            pane.Host.Child = null; // triggers DestroyWindowCore → render thread stop
            pane.MapView.Dispose();
        }
        while (_secondaries.Count < secondariesWanted)
        {
            var mapView = new MapView(_provider);
            var radar = new RadarDisplayController(mapView);
            // Secondary panes open on complementary products.
            int defaultKey = _secondaries.Count switch { 0 => 0x56, 1 => 0x44, _ => 0x43 }; // V, D, C
            radar.OnKey(defaultKey);
            mapView.KeyPressed += key => radar.OnKey(key);
            mapView.Camera.SetView(
                _primary.MapView.Camera.Snapshot().CenterX,
                _primary.MapView.Camera.Snapshot().CenterY,
                _primary.MapView.Camera.Snapshot().MetersPerPixel);

            var host = new Border { Child = new D3DHostControl(mapView) };
            _secondaries.Add(new Pane(mapView, radar, host));
            _grid.Children.Add(host);
            if (_currentVolume is not null)
                radar.ShowVolume(_currentVolume);
        }

        Relayout(count);
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

        var panes = new List<Pane> { _primary };
        panes.AddRange(_secondaries);

        // Whichever camera changed since last tick becomes the source of truth.
        int moved = -1;
        for (int i = 0; i < panes.Count; i++)
        {
            var snapshot = panes[i].MapView.Camera.Snapshot();
            if (snapshot.CenterX != _lastSnapshots[i].CenterX ||
                snapshot.CenterY != _lastSnapshots[i].CenterY ||
                snapshot.MetersPerPixel != _lastSnapshots[i].MetersPerPixel)
            {
                moved = i;
                break;
            }
        }
        if (moved >= 0)
        {
            var source = panes[moved].MapView.Camera.Snapshot();
            for (int i = 0; i < panes.Count; i++)
                if (i != moved)
                    panes[i].MapView.Camera.SetView(source.CenterX, source.CenterY, source.MetersPerPixel);
        }
        for (int i = 0; i < panes.Count; i++)
            _lastSnapshots[i] = panes[i].MapView.Camera.Snapshot();
    }

    public void Dispose()
    {
        _linkTimer.Stop();
        foreach (var pane in _secondaries)
        {
            pane.Host.Child = null;
            pane.MapView.Dispose();
        }
        _secondaries.Clear();
    }
}
