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
    private readonly StormOverlayController _storms = new();
    private readonly ThreatMonitor _threats = new();
    private OutlookOverlayController? _outlooks;
    private FutureRadarController? _future;
    private TrayNotifier? _tray;
    private PaneManager? _panes;
    private Geocoder? _geocoder;
    private AppSettings? _settings;
    private OverlayGeometry? _measureGeometry;
    private OverlayGeometry? _homeGeometry;
    private bool _suppressSliderEvents;
    private bool _settingHome;
    private int _paneCount = 1;
    private Popup? _stormPopup;
    private Popup? _toast;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        LinkToggle.IsEnabled = false; // only meaningful once a second pane exists

        var settings = _settings = AppSettings.Load();
        Units.System = settings.Units;
        _geocoder = new Geocoder(settings.UserAgent);
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

        _panes = new PaneManager(PaneGrid, _mapView, _radar, MapHost, provider);

        _playback = new ArchivePlaybackController(_mapView, _radar);
        _playback.VolumeLoaded += volume => _panes.ShowVolume(volume);
        _playback.StatusChanged += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);
        _playback.DayLoaded += (count, index) => Dispatcher.BeginInvoke(() =>
        {
            _suppressSliderEvents = true;
            TimeSlider.Maximum = count - 1;
            TimeSlider.Value = index;
            TimeSlider.IsEnabled = true;
            _suppressSliderEvents = false;
            UpdateSliderLabel();
            RebuildTicks();
        });
        _playback.PlayingChanged += playing => Dispatcher.BeginInvoke(() =>
            PlayButton.Content = playing ? "⏸" : "▶");

        foreach (var site in RadarSites.All.Where(s => !s.IsTdwr).OrderBy(s => s.Icao))
            SiteCombo.Items.Add(site);
        SiteCombo.SelectedItem = RadarSites.ByIcao("KTLX");
        DayPicker.SelectedDate = new DateTime(2013, 5, 20); // the committed demo day

        _warnings = new WarningsController(_mapView, MapHost, settings.UserAgent);
        _warnings.GeometryChanged += () => Dispatcher.BeginInvoke(ComposeOverlay);
        _warnings.StatusChanged += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);

        _outlooks = new OutlookOverlayController(settings.UserAgent);
        _outlooks.GeometryChanged += () => Dispatcher.BeginInvoke(ComposeOverlay);
        _outlooks.StatusChanged += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);

        _future = new FutureRadarController(_mapView, settings.UserAgent);
        _future.StatusChanged += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);
        _future.FrameChanged += (index, count, valid) => Dispatcher.BeginInvoke(() =>
        {
            // The newest complete HRRR run is an hour or so old, so its earliest frames
            // can already be behind us. Say so rather than printing "+-0.7 h".
            double ahead = (valid - DateTime.UtcNow).TotalHours;
            string when = ahead switch
            {
                < -0.25 => $"{-ahead:F1} h ago",
                < 0.25 => "about now",
                _ => $"+{ahead:F1} h",
            };
            FutureLabel.Text = $"{when} · {valid:HH:mm}Z (frame {index + 1} of {count})";
            foreach (var b in new[] { FuturePrevButton, FuturePlayButton, FutureNextButton, FutureClearButton })
                b.IsEnabled = true;
        });

        _tray = new TrayNotifier();
        _tray.Activated += () => Dispatcher.BeginInvoke(() =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });

        _storms.GeometryChanged += () => Dispatcher.BeginInvoke(ComposeOverlay);
        _storms.StatusChanged += text => Dispatcher.BeginInvoke(() => StatusText.Text = text);
        _storms.StormsUpdated += storms => Dispatcher.BeginInvoke(() => _threats.EvaluateStorms(storms));
        _warnings!.AlertsUpdated += alerts => Dispatcher.BeginInvoke(() => _threats.EvaluateWarnings(alerts));
        _threats.ThreatDetected += threat => Dispatcher.BeginInvoke(() => OnThreat(threat));
        _mapView.Clicked += RouteMapClick;

        _threats.Configure(settings.HomeLatDeg, settings.HomeLonDeg, settings.AlertRadiusKm);
        RadiusCombo.SelectedIndex = settings.AlertRadiusKm switch
        {
            <= 15 => 0, <= 40 => 1, <= 80 => 2, _ => 3,
        };
        RebuildHomeGeometry();
        UpdateHomeLabels();
        EnsureStormWatchForHome(); // startup with a saved home arms the storm watch immediately

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
                _panes!.ShowVolume(volume);
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
            _tray?.Dispose();
            _future?.Dispose();
            _outlooks?.Dispose();
            _geocoder?.Dispose();
            _storms.Dispose();
            _liveFeed.Dispose();
            _playback.Dispose();
            _panes?.Dispose();
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

        // Elapsed time only means something for a live feed. On archive data it produced
        // readings like "116106 h" — technically true, and useless.
        if (LiveToggle.IsChecked != true)
        {
            AgeText.Text = $"ARCHIVE  {time:yyyy-MM-dd HH:mm}Z";
            AgeText.Foreground = System.Windows.Media.Brushes.Gray;
            return;
        }

        var age = DateTime.UtcNow - time;
        AgeText.Text = age.TotalHours >= 1
            ? $"LIVE  {(int)age.TotalHours} h {age.Minutes:D2} m old"
            : $"LIVE  {(int)age.TotalMinutes} m {age.Seconds:D2} s old";
        AgeText.Foreground = age > TimeSpan.FromMinutes(10)
            ? System.Windows.Media.Brushes.OrangeRed
            : System.Windows.Media.Brushes.LightGreen;
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
                _panes!.ShowVolume(volume);
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

    /// <summary>Merge warning polygons, storm features, and the measure line into one overlay.</summary>
    private void ComposeOverlay()
    {
        var labels = new List<MapView.MapLabel>(_storms.Labels);
        if (_outlooks?.Labels is { Count: > 0 } outlookLabels)
            labels.AddRange(outlookLabels);
        _mapView.SetLabels(labels);

        OverlayGeometry?[] sources =
            [_outlooks?.Geometry, _warnings?.Geometry, _storms.Geometry, _homeGeometry, _measureGeometry];
        var active = sources.Where(s => s is not null).Cast<OverlayGeometry>().ToArray();
        switch (active.Length)
        {
            case 0:
                _mapView.SetOverlay(null);
                return;
            case 1:
                _mapView.SetOverlay(active[0]);
                return;
        }
        var merged = new OverlayGeometry();
        foreach (var source in active)
        {
            merged.FillTriangles.AddRange(source.FillTriangles);
            merged.Lines.AddRange(source.Lines);
        }
        _mapView.SetOverlay(merged);
    }

    /// <summary>The site the storm layer should watch: nearest to home when home is set,
    /// otherwise whatever is selected for browsing.</summary>
    private RadarSite? StormWatchSite()
    {
        if (_settings?.HomeLatDeg is { } lat && _settings.HomeLonDeg is { } lon)
            return RadarSites.Nearest(lat, lon);
        return SiteCombo.SelectedItem as RadarSite;
    }

    /// <summary>Home is set: make sure the storm layer is watching its nearest radar,
    /// so track alerts work without a manual toggle.</summary>
    private void EnsureStormWatchForHome()
    {
        if (_settings?.HomeLatDeg is null || StormWatchSite() is not { } site) return;
        if (StormsToggle.IsChecked == true)
            _storms.Enable(site.Icao); // re-point (home may have moved to a new nearest site)
        else
            StormsToggle.IsChecked = true; // fires StormsToggle_Checked
    }

    private void StormsToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (StormWatchSite() is { } site)
            _storms.Enable(site.Icao);
        else
            StormsToggle.IsChecked = false;
    }

    private void StormsToggle_Unchecked(object sender, RoutedEventArgs e) => _storms.Disable();

    // ---- click routing: set-home > storm details > warning details ----

    private void RouteMapClick(int x, int y)
    {
        _stormPopup?.IsOpen = false;
        _stormPopup = null;

        var (lat, lon) = _mapView.ScreenToLatLon(x, y);
        if (_settingHome)
        {
            _settingHome = false;
            SetHomeButton.Content = "📍 Set home on map";
            _settings!.HomeLatDeg = lat;
            _settings.HomeLonDeg = lon;
            _settings.Save();
            _threats.Configure(lat, lon, _settings.AlertRadiusKm);
            EnsureStormWatchForHome();
            RebuildHomeGeometry();
            UpdateHomeLabels();
            ComposeOverlay();
            StatusText.Text = $"Home set to {lat:F3}, {lon:F3} — proximity alerts armed, " +
                              $"storm watch on {StormWatchSite()?.Icao}.";
            return;
        }

        if (_storms.HitTest(lat, lon) is { } storm)
        {
            ShowStormPopup(storm, x, y);
            return;
        }

        _warnings?.HandleClick(x, y);
    }

    private void ShowStormPopup(TrackedStorm storm, int x, int y)
    {
        var lines = new List<string>
        {
            $"Moving {ThreatMonitor.CompassPoint(storm.BearingDeg)} ({storm.BearingDeg:F0}°) at {Units.Speed(storm.SpeedKmh)}",
        };
        if (storm.ProbabilityOfHail > 0)
            lines.Add($"Hail {storm.ProbabilityOfHail}% · severe {Math.Max(0, storm.ProbabilityOfSevereHail)}%" +
                      (storm.MaxHailSizeInches > 0 ? $" · max {storm.MaxHailSizeInches}\"" : ""));
        if (storm.MaxDbz is { } maxDbz)
            lines.Add($"Max reflectivity {maxDbz} dBZ" +
                      (storm.EchoTopKft is { } top ? $" · echo top {Units.HeightKft(top)}" : "") +
                      (storm.CellBasedVil is { } vil ? $" · VIL {vil:F0}" : ""));
        if (storm.MesoRadiusKm is { } mesoRadius)
            lines.Add($"MESOCYCLONE — radius {Units.Distance(mesoRadius)}");
        if (_threats.IsArmed &&
            ThreatMonitor.ClosestApproach(storm, _threats.HomeLatDeg!.Value, _threats.HomeLonDeg!.Value)
                is { } approach)
            lines.Add(approach.DistanceKm <= _threats.RadiusKm
                ? $"⚠ Passes {Units.Distance(approach.DistanceKm)} from home in ~{approach.EtaMinutes:F0} min"
                : $"Closest approach to home: {Units.Distance(approach.DistanceKm)}");

        var panel = new StackPanel { MaxWidth = 340, Margin = new Thickness(10) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Storm {storm.Id}",
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
        });
        foreach (var line in lines)
            panel.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = System.Windows.Media.Brushes.Gainsboro,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });

        _stormPopup = new Popup
        {
            PlacementTarget = MapHost,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = x + 12,
            VerticalOffset = y + 12,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(240, 26, 28, 33)),
                BorderBrush = System.Windows.Media.Brushes.WhiteSmoke,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                Child = panel,
            },
            IsOpen = true,
        };
    }

    // ---- home area + proximity alerts ----

    private void RebuildHomeGeometry()
    {
        if (_settings?.HomeLatDeg is not { } lat || _settings.HomeLonDeg is not { } lon)
        {
            _homeGeometry = null;
            return;
        }
        var geometry = new OverlayGeometry();
        var centre = GeoMath.ToMercator(lat, lon);
        uint color = OverlayGeometry.Pack(80, 200, 255, 235);
        // Radius ring drawn in true kilometers; Mercator inflates by 1/cos(lat).
        double mercatorRadius = _settings.AlertRadiusKm * 1000.0 / Math.Cos(lat * Math.PI / 180.0);
        StormOverlayController.AddCircle(geometry, (centre.X, centre.Y), mercatorRadius, color, 2f);
        double s = 700;
        geometry.FillTriangles.Add((centre.X - s, centre.Y - s, color));
        geometry.FillTriangles.Add((centre.X, centre.Y + s, color));
        geometry.FillTriangles.Add((centre.X + s, centre.Y - s, color));
        _homeGeometry = geometry;
    }

    private void UpdateHomeLabels()
    {
        if (_settings?.HomeLatDeg is { } lat && _settings.HomeLonDeg is { } lon)
        {
            HomeLabel.Text = $"Home: {lat:F3}, {lon:F3}";
            AlertArmedLabel.Text =
                $"Alerts armed ({Units.Distance(_settings.AlertRadiusKm)}). " +
                $"Storm watch auto-enabled on {StormWatchSite()?.Icao ?? "?"} " +
                "(nearest radar to home); warning alerts always on.";
        }
        else
        {
            HomeLabel.Text = "Home: not set";
            AlertArmedLabel.Text = "Set a home location to arm proximity alerts.";
        }
    }

    private void OnThreat(Threat threat)
    {
        System.Media.SystemSounds.Exclamation.Play();
        StatusText.Text = $"⚠ {threat.Title}";

        // A toast inside the window is invisible when the window is not. Always send a
        // tray notification too — this is the case proximity alerts exist for.
        _tray?.Notify(threat.Title, threat.Detail, threat.IsTornado);

        _toast?.IsOpen = false;
        var panel = new StackPanel { MaxWidth = 360, Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = $"⚠ {threat.Title}",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = threat.IsTornado
                ? System.Windows.Media.Brushes.OrangeRed
                : System.Windows.Media.Brushes.Gold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = threat.Detail,
            Foreground = System.Windows.Media.Brushes.Gainsboro,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var toast = new Popup
        {
            PlacementTarget = MapHost,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = Math.Max(8, MapHost.ActualWidth - 392),
            VerticalOffset = Math.Max(8, MapHost.ActualHeight - 130),
            StaysOpen = true,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(245, 33, 26, 26)),
                BorderBrush = threat.IsTornado
                    ? System.Windows.Media.Brushes.OrangeRed
                    : System.Windows.Media.Brushes.Gold,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Child = panel,
            },
            IsOpen = true,
        };
        _toast = toast;
        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            toast.IsOpen = false;
            if (ReferenceEquals(_toast, toast)) _toast = null;
        };
        closeTimer.Start();
    }

    // ---- layers panel handlers ----

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is not null)
            _mapView.RadarOpacity = (float)(e.NewValue / 100.0);
    }

    private void WarningFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_warnings is null) return;
        _warnings.ShowTornado = FilterTornado.IsChecked == true;
        _warnings.ShowSevereThunderstorm = FilterSevere.IsChecked == true;
        _warnings.ShowFlashFlood = FilterFlood.IsChecked == true;
        _warnings.ShowOther = FilterOtherWarn.IsChecked == true;
        _warnings.Rebuild();
    }

    private void StormFilter_Changed(object sender, RoutedEventArgs e) => ApplyStormFilters();

    private void StormFilter_SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ApplyStormFilters();

    private void ApplyStormFilters()
    {
        // Fires during XAML parse; later-declared controls may not exist yet.
        if (_storms is null || FilterPastTrack is null || FilterForecastTrack is null ||
            FilterCones is null || FilterLabels is null ||
            FilterHail is null || FilterMeso is null || PoshSlider is null) return;
        _storms.ShowPastTrack = FilterPastTrack.IsChecked == true;
        _storms.ShowForecastTrack = FilterForecastTrack.IsChecked == true;
        _storms.ShowCones = FilterCones.IsChecked == true;
        _storms.ShowLabels = FilterLabels.IsChecked == true;
        _storms.ShowHail = FilterHail.IsChecked == true;
        _storms.ShowMeso = FilterMeso.IsChecked == true;
        _storms.MinSevereHailProbability = (int)PoshSlider.Value;
        _storms.Rebuild();
    }

    private void MosaicFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_mapView is null) return;
        _mapView.MosaicEnabled = FilterMosaic.IsChecked == true;
    }

    private void MosaicOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is not null)
            _mapView.MosaicOpacity = (float)(e.NewValue / 100.0);
    }

    private void SatelliteFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_mapView is not null)
            _mapView.SatelliteEnabled = FilterSatellite.IsChecked == true;
    }

    // ---- future radar ----

    private async void FutureLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_future is null) return;
        FutureLoadButton.IsEnabled = false;
        try
        {
            await _future.LoadAsync();
        }
        finally
        {
            FutureLoadButton.IsEnabled = true;
        }
    }

    private void FuturePrev_Click(object sender, RoutedEventArgs e) => _future?.Step(-1);

    private void FutureNext_Click(object sender, RoutedEventArgs e) => _future?.Step(1);

    private void FuturePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_future is null) return;
        if (_future.IsPlaying)
        {
            _future.Pause();
            FuturePlayButton.Content = "▶";
        }
        else
        {
            _future.Play();
            FuturePlayButton.Content = "⏸";
        }
    }

    private void FutureClear_Click(object sender, RoutedEventArgs e)
    {
        _future?.Clear();
        FuturePlayButton.Content = "▶";
        FutureLabel.Text = "Not loaded";
        foreach (var b in new[] { FuturePrevButton, FuturePlayButton, FutureNextButton, FutureClearButton })
            b.IsEnabled = false;
    }

    private async void OutlookFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_outlooks is null || FilterOutlooks is null || FilterWatches is null ||
            FilterDiscussions is null || FilterReports is null) return;
        _outlooks.ShowOutlooks = FilterOutlooks.IsChecked == true;
        _outlooks.ShowWatches = FilterWatches.IsChecked == true;
        _outlooks.ShowDiscussions = FilterDiscussions.IsChecked == true;
        _outlooks.ShowReports = FilterReports.IsChecked == true;
        await _outlooks.ApplyAsync();
    }

    private void SmoothSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is not null)
            _mapView.RadarSmoothing = (float)(e.NewValue / 100.0);
    }

    private void SetHomeButton_Click(object sender, RoutedEventArgs e)
    {
        _settingHome = !_settingHome;
        SetHomeButton.Content = _settingHome ? "Click the map…" : "📍 Set home on map";
        if (_settingHome)
            StatusText.Text = "Click the map to set your home location.";
    }

    private void RadiusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings is null) return;
        _settings.AlertRadiusKm = RadiusCombo.SelectedIndex switch
        {
            0 => 15, 2 => 80, 3 => 160, _ => 40,
        };
        _settings.Save();
        _threats.Configure(_settings.HomeLatDeg, _settings.HomeLonDeg, _settings.AlertRadiusKm);
        RebuildHomeGeometry();
        UpdateHomeLabels();
        ComposeOverlay();
    }


    private void LinkToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_panes is not null)
            _panes.LinkedPan = LinkToggle.IsChecked == true;
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

    /// <summary>
    /// Find the strongest cell in the country and point everything at it: browsing
    /// site, live feed, storm overlay (temporarily overriding the home watch), camera.
    /// The one-click way to exercise every feature against real weather.
    /// </summary>
    private async void HotspotButton_Click(object sender, RoutedEventArgs e)
    {
        HotspotButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Scanning every radar site for the heaviest precipitation…";
            var hotspot = await NationalStormScan.FindHeaviestAsync((done, total) =>
                Dispatcher.BeginInvoke(() =>
                    StatusText.Text = $"Scanning storm structure… {done}/{total} sites"));
            if (hotspot is null)
            {
                StatusText.Text = "No fresh storm cells anywhere in the USA — remarkably quiet.";
                return;
            }

            SiteCombo.SelectedItem = RadarSites.ByIcao(hotspot.Site.Icao);
            if (LiveToggle.IsChecked == true)
                LiveToggle.IsChecked = false; // restart the feed on the new site
            LiveToggle.IsChecked = true;
            StormsToggle.IsChecked = true;
            _storms.Enable(hotspot.Site.Icao); // follow the hotspot (overrides home watch for now)
            _mapView.Camera.MoveTo(hotspot.LatDeg, hotspot.LonDeg, 150);

            StatusText.Text =
                $"🎯 Hotspot: VIL {hotspot.MaxVilKgM2:F0} kg/m² near {hotspot.Site.Icao} " +
                $"({hotspot.Site.Name}, {hotspot.Site.State}), {Units.Distance(hotspot.RangeKm)} out — " +
                $"as of {hotspot.ProductTimeUtc:HH:mm}Z";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Hotspot scan failed: {ex.Message}";
        }
        finally
        {
            HotspotButton.IsEnabled = true;
        }
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var version = GetType().Assembly.GetName().Version?.ToString(3) ?? "dev";
        MessageBox.Show(this,
            $"""
            OpenWSR {version}
            An open-source native NEXRAD Level II radar viewer.

            NOT FOR LIFE-SAFETY DECISIONS.
            This software is provided for informational and educational use only.
            Never rely on it for warnings or protective action — use official
            National Weather Service products and local warning systems.

            Data: NOAA NEXRAD via AWS Open Data (NSF Unidata), NWS api.weather.gov.
            Basemap © OpenStreetMap contributors.
            See THIRD-PARTY-NOTICES.md for component licenses.

            Keys over the map: R/V/W/D/P/C moment · ↑/↓ tilt
            Right-drag: measure distance/bearing · Hover: inspector
            """,
            "About OpenWSR", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LayersToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (LayersPanel is not null)
            LayersPanel.Visibility = LayersToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Rail button cycles 1 → 2 → 4 panes, so the rail needs no dropdown.</summary>
    private void PaneButton_Click(object sender, RoutedEventArgs e)
    {
        _paneCount = _paneCount switch { 1 => 2, 2 => 4, _ => 1 };
        _panes?.SetPaneCount(_paneCount);
        PaneButton.Content = _paneCount switch { 1 => "◱", 2 => "◫", _ => "⊞" };
        PaneButton.ToolTip = $"Map panes: {_paneCount}";
        LinkToggle.IsEnabled = _paneCount > 1;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        Units.System = _settings.Units;
        StatusText.Text = "Settings saved. Basemap changes take effect next launch.";
        UpdateHomeLabels();
        _radar.Refresh();
    }

    // ---- location search ----

    private async void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;

        SearchBox.IsEnabled = false;
        StatusText.Text = $"Looking up “{query}”…";
        try
        {
            var place = await _geocoder!.SearchAsync(query);
            if (place is null)
            {
                StatusText.Text = $"No match for “{query}”. Try a city, a ZIP code, or lat,lon.";
                return;
            }
            var site = RadarSites.Nearest(place.LatDeg, place.LonDeg);
            double km = GeoMath.DistanceM(place.LatDeg, place.LonDeg, site.LatDeg, site.LonDeg) / 1000.0;
            SiteCombo.SelectedItem = RadarSites.ByIcao(site.Icao);
            _mapView.Camera.MoveTo(place.LatDeg, place.LonDeg, 220);
            StatusText.Text = $"{place.Name} — nearest radar {site.Icao} ({site.Name}), {Units.Distance(km)} away.";
            SearchBox.Clear();
        }
        finally
        {
            SearchBox.IsEnabled = true;
        }
    }

    // ---- timeline ticks ----

    private void TimelineArea_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildTicks();

    /// <summary>
    /// Hour ticks with labels under the scrub slider, so the timeline reads as a clock
    /// rather than an anonymous 0..N range.
    /// </summary>
    private void RebuildTicks()
    {
        TickCanvas.Children.Clear();
        var volumes = _playback?.DayVolumes;
        if (volumes is not { Count: > 1 }) return;

        double width = TimelineArea.ActualWidth;
        if (width < 60) return;

        var start = volumes[0].TimeUtc;
        var end = volumes[^1].TimeUtc;
        double totalMinutes = (end - start).TotalMinutes;
        if (totalMinutes <= 0) return;

        // Aim for roughly one label per 110 px.
        int[] candidates = [1, 2, 3, 4, 6, 8, 12];
        int hourStep = candidates[^1];
        foreach (var candidate in candidates)
        {
            if (totalMinutes / 60.0 / candidate * 110 <= width) { hourStep = candidate; break; }
        }

        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x6C, 0x74, 0x83));
        var tickHour = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        if (tickHour < start) tickHour = tickHour.AddHours(1);
        while (tickHour.Hour % hourStep != 0) tickHour = tickHour.AddHours(1);

        for (; tickHour <= end; tickHour = tickHour.AddHours(hourStep))
        {
            double fraction = (tickHour - start).TotalMinutes / totalMinutes;
            double x = fraction * width;
            TickCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = x, X2 = x, Y1 = 7, Y2 = 13,
                Stroke = brush, StrokeThickness = 1,
            });
            var label = new TextBlock
            {
                Text = $"{tickHour:HH}z",
                FontSize = 9.5,
                Foreground = brush,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Max(0, Math.Min(width - label.DesiredSize.Width, x - label.DesiredSize.Width / 2)));
            Canvas.SetTop(label, -2);
            TickCanvas.Children.Add(label);
        }
    }
}
