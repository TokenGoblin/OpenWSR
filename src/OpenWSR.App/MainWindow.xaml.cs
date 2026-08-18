using System.Windows;
using System.Windows.Threading;
using OpenWSR.Render;
using OpenWSR.Render.Tiles;

namespace OpenWSR.App;

public partial class MainWindow : Window
{
    private readonly MapView _mapView;
    private readonly DispatcherTimer _statusTimer;

    // Alignment check markers for the Phase 3 acceptance test: radar site positions
    // must land on the right cities. Replaced by the full site table in Phase 5.
    private static readonly (double Lat, double Lon, string Name)[] DebugSites =
    [
        (35.3331, -97.2775, "KTLX (Oklahoma City)"),
        (40.8655, -72.8639, "KOKX (New York)"),
        (48.1946, -122.4958, "KATX (Seattle)"),
        (25.6111, -80.4128, "KAMX (Miami)"),
        (39.7866, -104.5458, "KFTG (Denver)"),
        (41.6044, -88.0844, "KLOT (Chicago)"),
        (37.1550, -121.8980, "KMUX (San Francisco Bay)"),
        (29.7039, -98.0286, "KEWX (San Antonio)"),
    ];

    public MainWindow()
    {
        InitializeComponent();

        var settings = AppSettings.Load();
        var provider = settings.TileProvider == "maptiler" && !string.IsNullOrEmpty(settings.MapTilerKey)
            ? TileProvider.MapTiler(settings.MapTilerKey, settings.UserAgent)
            : TileProvider.Osm(settings.UserAgent);
        AttributionText.Text = provider.Name == "maptiler"
            ? "© MapTiler © OpenStreetMap contributors"
            : "© OpenStreetMap contributors";

        _mapView = new MapView(provider);
        _mapView.Camera.MoveTo(39.0, -98.0, 6000); // continental US
        _mapView.SetMarkers(DebugSites.Select(s => (s.Lat, s.Lon)));
        MapHost.Child = new D3DHostControl(_mapView);

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statusTimer.Tick += (_, _) => FpsText.Text = $"{_mapView.FramesPerSecond:F0} fps";
        _statusTimer.Start();

        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _mapView.Dispose();
        };
    }
}
