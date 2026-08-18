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
    private readonly LiveFeed _liveFeed = new();
    private WarningsController? _warnings;
    private InspectorTools? _inspector;
    private OverlayGeometry? _measureGeometry;
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

        _warnings = new WarningsController(_mapView, MapHost, settings.UserAgent);
        _warnings.GeometryChanged += () => Dispatcher.BeginInvoke(ComposeOverlay);
        _warnings.StatusChanged += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);

        _inspector = new InspectorTools(_mapView, _radar);
        _inspector.InspectorChanged += text => Dispatcher.BeginInvoke(() => InspectorText.Text = text);
        _inspector.MeasureChanged += geometry =>
        {
            _measureGeometry = geometry;
            Dispatcher.BeginInvoke(ComposeOverlay);
        };

        _liveFeed.StatusChanged += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);
        _liveFeed.VolumeUpdated += (volume, _) => Dispatcher.BeginInvoke(() =>
        {
            if (LiveToggle.IsChecked == true)
                _radar.ShowVolume(volume);
        });

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statusTimer.Tick += (_, _) =>
        {
            FpsText.Text = $"{_mapView.FramesPerSecond:F0} fps · sweep upload {_mapView.LastSweepUploadMs:F1} ms";
            UpdateAgeIndicator();
        };
        _statusTimer.Start();

        Loaded += async (_, _) => await LoadStartupAsync();
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _liveFeed.Dispose();
            _playback.Dispose();
            _mapView.Dispose();
        };
    }

    /// <summary>
    /// A stale radar image with no indication the feed died is dangerous. The age of the
    /// displayed data is always visible; in live mode it turns amber past 10 minutes.
    /// </summary>
    private void UpdateAgeIndicator()
    {
        if (_radar.DisplayedSweepTimeUtc is not { } time)
        {
            AgeText.Text = "";
            return;
        }
        var age = DateTime.UtcNow - time;
        bool live = LiveToggle.IsChecked == true;
        AgeText.Text = age.TotalHours >= 1
            ? $"data age {(int)age.TotalHours} h {age.Minutes:D2} m"
            : $"data age {(int)age.TotalMinutes} m {age.Seconds:D2} s";
        AgeText.Foreground = live && age > TimeSpan.FromMinutes(10)
            ? System.Windows.Media.Brushes.OrangeRed
            : live
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.Gray;
    }

    private void LiveToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (SiteCombo.SelectedItem is not RadarSite site)
        {
            LiveToggle.IsChecked = false;
            return;
        }
        _playback.StopLoop();
        TimeSlider.IsEnabled = false;
        _liveFeed.Start(site.Icao);
        _mapView.Camera.MoveTo(site.LatDeg, site.LonDeg, 250);
    }

    private void LiveToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _liveFeed.Stop();
        TimeSlider.IsEnabled = _playback.DayVolumes.Count > 0;
        StatusText.Text = "Live feed stopped.";
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

    /// <summary>Merge warning polygons and the transient measure line into one overlay.</summary>
    private void ComposeOverlay()
    {
        var warnings = _warnings?.Geometry;
        if (_measureGeometry is null)
        {
            _mapView.SetOverlay(warnings);
            return;
        }
        var merged = new OverlayGeometry();
        if (warnings is not null)
        {
            merged.FillTriangles.AddRange(warnings.FillTriangles);
            merged.Lines.AddRange(warnings.Lines);
        }
        merged.FillTriangles.AddRange(_measureGeometry.FillTriangles);
        merged.Lines.AddRange(_measureGeometry.Lines);
        _mapView.SetOverlay(merged);
    }

    private void PaletteButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "GR2Analyst color tables (*.pal)|*.pal|All files (*.*)|*.*",
            Title = $"Import palette for {_radar.CurrentMoment}",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _radar.SetCustomTable(OpenWSR.Palettes.Gr2Palette.ParseFile(dialog.FileName));
            StatusText.Text = $"Palette applied to {_radar.CurrentMoment}: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Palette import failed: {ex.Message}";
        }
    }

    private void ToolBar_Loaded(object sender, RoutedEventArgs e)
    {
        // Hide the toolbar overflow chevron; everything fits.
        if (sender is ToolBar toolBar && toolBar.Template.FindName("OverflowGrid", toolBar) is FrameworkElement grid)
            grid.Visibility = Visibility.Collapsed;
    }
}
