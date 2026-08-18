using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.Nexrad;
using OpenWSR.Render;
using OpenWSR.Render.Tiles;

namespace OpenWSR.App;

public partial class MainWindow : Window
{
    private readonly MapView _mapView;
    private readonly DispatcherTimer _statusTimer;
    private readonly RadarDisplayController _radar;
    private readonly ArchivePlaybackController _playback;
    private bool _suppressSliderEvents;

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
        _mapView.SetMarkers(RadarSites.All.Where(s => !s.IsTdwr).Select(s => (s.LatDeg, s.LonDeg)));
        MapHost.Child = new D3DHostControl(_mapView);

        _radar = new RadarDisplayController(_mapView);
        _radar.StatusChanged += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);
        _mapView.KeyPressed += key => _radar.OnKey(key);
        KeyDown += (_, e) => _radar.OnKey(System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key));

        _playback = new ArchivePlaybackController(_mapView, _radar);
        _playback.StatusChanged += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);
        _playback.DayLoaded += (count, index) => Dispatcher.BeginInvoke(() =>
        {
            _suppressSliderEvents = true;
            TimeSlider.Maximum = count - 1;
            TimeSlider.Value = index;
            TimeSlider.IsEnabled = true;
            _suppressSliderEvents = false;
            UpdateSliderLabel();
        });
        _playback.PlayingChanged += playing => Dispatcher.BeginInvoke(() =>
            PlayButton.Content = playing ? "⏸ Stop" : "▶ Loop");

        foreach (var site in RadarSites.All.Where(s => !s.IsTdwr).OrderBy(s => s.Icao))
            SiteCombo.Items.Add(site);
        SiteCombo.SelectedItem = RadarSites.ByIcao("KTLX");
        DayPicker.SelectedDate = new DateTime(2013, 5, 20); // the committed demo day

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statusTimer.Tick += (_, _) =>
            FpsText.Text = $"{_mapView.FramesPerSecond:F0} fps · sweep upload {_mapView.LastSweepUploadMs:F1} ms";
        _statusTimer.Start();

        Loaded += async (_, _) => await LoadStartupAsync();
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _playback.Dispose();
            _mapView.Dispose();
        };
    }

    private async Task LoadStartupAsync()
    {
        // A file passed on the command line bypasses the archive browser.
        string? path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (path is not null && File.Exists(path))
        {
            StatusText.Text = $"Decoding {Path.GetFileName(path)}…";
            try
            {
                var volume = await Task.Run(() => ArchiveFile.DecodeFile(path));
                _mapView.Camera.MoveTo(volume.LatDeg, volume.LonDeg, 250);
                _radar.ShowVolume(volume);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Decode failed: {ex.Message}";
            }
            return;
        }
        await LoadSelectedDayAsync();
    }

    private async Task LoadSelectedDayAsync()
    {
        if (SiteCombo.SelectedItem is not RadarSite site || DayPicker.SelectedDate is not { } day)
            return;
        await _playback.LoadDayAsync(site.Icao, DateOnly.FromDateTime(day));
    }

    private void UpdateSliderLabel()
    {
        int index = (int)TimeSlider.Value;
        TimeSliderLabel.Text = index >= 0 && index < _playback.DayVolumes.Count
            ? $"{_playback.DayVolumes[index].TimeUtc:HH:mm:ss}Z  ({index + 1}/{_playback.DayVolumes.Count})"
            : "—";
    }

    private async void LoadDayButton_Click(object sender, RoutedEventArgs e) =>
        await LoadSelectedDayAsync();

    private void NearestButton_Click(object sender, RoutedEventArgs e)
    {
        var cam = _mapView.Camera.Snapshot();
        var (lat, lon) = GeoMath.FromMercator(cam.CenterX, cam.CenterY);
        SiteCombo.SelectedItem = RadarSites.Nearest(lat, lon);
    }

    private async void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        UpdateSliderLabel();
        await _playback.ScrubToAsync((int)e.NewValue);
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.IsPlaying)
            _playback.StopLoop();
        else
            await _playback.PlayAsync();
    }

    private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_playback is null) return;
        _playback.SetSpeed(SpeedCombo.SelectedIndex switch { 0 => 2, 2 => 8, _ => 4 });
    }

    private void ToolBar_Loaded(object sender, RoutedEventArgs e)
    {
        // Hide the toolbar overflow chevron; everything fits.
        if (sender is ToolBar toolBar && toolBar.Template.FindName("OverflowGrid", toolBar) is FrameworkElement grid)
            grid.Visibility = Visibility.Collapsed;
    }
}
