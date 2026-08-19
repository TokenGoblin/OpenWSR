using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OpenWSR.Geo;
using OpenWSR.Ingest;
using OpenWSR.Nexrad;
using OpenWSR.Nexrad.Analysis;
using OpenWSR.Render;
using OpenWSR.Render.Tiles;

namespace OpenWSR.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly MapView _mapView;
    private readonly DispatcherTimer _statusTimer;
    private readonly RadarDisplayController _radar;
    private readonly ArchivePlaybackController _playback;
    private readonly LiveFeed _liveFeed = new();
    private readonly WarningsController _warnings;
    private readonly InspectorTools _inspector;
    private readonly StormOverlayController _storms;
    private readonly ThreatMonitor _threats = new();
    private readonly OutlookOverlayController _outlooks;
    private readonly FutureRadarController _future;
    private readonly PlacefileController _placefiles;
    private readonly RotationTracksController _tracks;
    private readonly LightningController _lightning;
    private readonly MrmsController _mrms;
    private readonly TrayNotifier _tray;
    private readonly PaneManager _panes;
    private readonly Geocoder _geocoder;
    private readonly AppSettings _settings;
    private OverlayGeometry? _measureGeometry;
    private readonly DrawingController _drawing;
    private OverlayGeometry? _homeGeometry;
    private bool _suppressSliderEvents;
    private bool _suppressModeEvents;
    private bool _suppressDayEvents;
    private int _paneCount = 1;
    private Popup? _stormPopup;
    private Popup? _toast;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        MomentBar.ItemsSource = _vm.Moments;
        TiltCombo.ItemsSource = _vm.Tilts;
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
        _mapView.SetMarkers(RadarSites.All.Where(s => !s.IsTdwr)
            .Select(s => (s.LatDeg, s.LonDeg, s.Icao)));
        _mapView.MarkersEnabled = settings.ShowSiteMarkers;
        FilterSites.IsChecked = settings.ShowSiteMarkers;
        MapHost.Child = new D3DHostControl(_mapView);

        _radar = new RadarDisplayController(_mapView);
        _radar.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));
        _radar.SelectionChanged += () => Dispatcher.BeginInvoke(() =>
        {
            SyncProductBar();
            RebuildWindProfile();
        });

        // One place interprets map keys. Wiring both this and Window.KeyDown to the same
        // handler double-stepped the tilt and let text typed into the search box change
        // the product; PreviewKeyDown below covers the WPF-focus case with a guard.
        _mapView.KeyPressed += key =>
        {
            if (_vm.Tool == MapTool.Draw && _drawing.OnKey(key)) return;
            _radar.OnKey(key);
        };
        PreviewKeyDown += Window_PreviewKeyDown;

        _panes = new PaneManager(PaneGrid, _mapView, _radar, MapHost, provider);
        _panes.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));

        _playback = new ArchivePlaybackController(_mapView, _radar) { LoopFrames = _settings.LoopFrames };
        _playback.VolumeLoaded += volume =>
        {
            _panes.ShowVolume(volume);
            SyncPaneContext();
        };
        _playback.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));
        _playback.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));
        _playback.ProgressChanged += value => Dispatcher.BeginInvoke(() => ShowProgress(value));
        _playback.LoopFrameShown += index => Dispatcher.BeginInvoke(() => SyncSliderToLoop(index));
        _playback.DayLoaded += (count, index) => Dispatcher.BeginInvoke(() =>
        {
            _suppressSliderEvents = true;
            TimeSlider.Maximum = Math.Max(0, count - 1);
            TimeSlider.Value = index;
            TimeSlider.IsEnabled = count > 0;
            _suppressSliderEvents = false;
            UpdateSliderLabel();
            RebuildTicks();
        });
        _playback.PlayingChanged += playing => Dispatcher.BeginInvoke(() =>
            PlayButton.Content = playing ? "⏸" : "▶");

        foreach (var site in RadarSites.All.Where(s => !s.IsTdwr).OrderBy(s => s.Icao))
            SiteCombo.Items.Add(site);

        _warnings = new WarningsController(_mapView, MapHost, settings.UserAgent);
        _warnings.GeometryChanged += () => Dispatcher.BeginInvoke(ComposeOverlay);
        _warnings.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));
        _warnings.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));

        _outlooks = new OutlookOverlayController(settings.UserAgent);
        _outlooks.GeometryChanged += () => Dispatcher.BeginInvoke(ComposeOverlay);
        _outlooks.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));

        _placefiles = new PlacefileController(_mapView, settings.UserAgent);
        _placefiles.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            PlacefileList.ItemsSource = null;
            PlacefileList.ItemsSource = _placefiles.Files;
            ComposeOverlay();
        });
        _placefiles.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));

        _future = new FutureRadarController(_mapView, settings.UserAgent);
        _future.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));
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

        _tracks = new RotationTracksController(_mapView);
        _tracks.StatusChanged += text => Dispatcher.BeginInvoke(() =>
        {
            Report(text);
            TracksStatusText.Text = text;
        });
        _tracks.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));
        _tracks.ProgressChanged += value => Dispatcher.BeginInvoke(() => ShowProgress(value));

        _lightning = new LightningController(_mapView);
        _lightning.GeometryChanged += () => Dispatcher.BeginInvoke(ComposeOverlay);
        _lightning.StatusChanged += text => Dispatcher.BeginInvoke(() =>
        {
            Report(text);
            LightningNoteText.Text = text;
        });
        _lightning.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));

        _mrms = new MrmsController(_mapView);
        _mrms.StatusChanged += text => Dispatcher.BeginInvoke(() =>
        {
            Report(text);
            MrmsNoteText.Text = text;
        });
        _mrms.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));

        _tray = new TrayNotifier();
        _tray.Activated += () => Dispatcher.BeginInvoke(() =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });

        _storms = new StormOverlayController(_mapView);
        _storms.GeometryChanged += () => Dispatcher.BeginInvoke(ComposeOverlay);
        _storms.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));
        _storms.StormsUpdated += storms => Dispatcher.BeginInvoke(() =>
        {
            _threats.EvaluateStorms(storms);
            UpdateStormMotion(storms);
            UpdateStormNote(storms);
        });
        _warnings.AlertsUpdated += alerts => Dispatcher.BeginInvoke(() => _threats.EvaluateWarnings(alerts));
        _threats.ThreatDetected += threat => Dispatcher.BeginInvoke(() => OnThreat(threat));
        _drawing = new DrawingController(_mapView);
        _drawing.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            ComposeOverlay();
            SyncDrawingButtons();
        });
        _drawing.HintChanged += text => Dispatcher.BeginInvoke(() => DrawHint.Text = text);

        _mapView.Clicked += RouteMapClick;

        _threats.Configure(settings.HomeLatDeg, settings.HomeLonDeg, settings.AlertRadiusKm);
        RebuildHomeGeometry();
        EnsureStormWatchForHome(); // startup with a saved home arms the storm watch immediately

        _mapView.MeasureDragged += (sx, sy, x, y, finished) =>
        {
            if (!finished || _vm.Tool != MapTool.CrossSection) return;
            var start = _mapView.ScreenToLatLon(sx, sy);
            var end = _mapView.ScreenToLatLon(x, y);
            Dispatcher.BeginInvoke(() =>
                BuildCrossSection(start.LatDeg, start.LonDeg, end.LatDeg, end.LonDeg));
        };

        _inspector = new InspectorTools(_mapView, _radar);
        _inspector.InspectorChanged += text => Dispatcher.BeginInvoke(() => InspectorText.Text = text);
        _inspector.MeasureChanged += geometry =>
        {
            _measureGeometry = geometry;
            Dispatcher.BeginInvoke(ComposeOverlay);
        };

        _liveFeed.StatusChanged += text => Dispatcher.BeginInvoke(() =>
        {
            Report(text);
            if (_vm.IsLive) LiveStateText.Text = text;
        });
        _liveFeed.VolumeUpdated += (volume, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_vm.IsLive) return;
            _panes.ShowVolume(volume);
            SyncPaneContext();
        });

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statusTimer.Tick += (_, _) =>
        {
            FpsText.Text = $"{_mapView.FramesPerSecond:F0} fps · sweep upload {_mapView.LastSweepUploadMs:F1} ms";
            UpdateAgeIndicator();
            // Storm symbols are sized in screen pixels, so a zoom change means new geometry.
            _storms.NotifyViewChanged();
            _lightning.NotifyViewChanged();
            _mrms.NotifyViewChanged();
        };
        _statusTimer.Start();

        ApplyMode(DataMode.Archive, initial: true);
        SyncProductBar();
        SyncToolButtons();

        Loaded += async (_, _) =>
        {
            SyncLoopTooltip();
            foreach (var source in settings.Placefiles.ToList())
                await _placefiles.AddAsync(source);
            await LoadStartupAsync();
            if (!settings.WelcomeShown)
            {
                settings.WelcomeShown = true;
                settings.Save();
                InfoWindow.ShowShortcuts(this);
            }
        };
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _tray.Dispose();
            _placefiles.Dispose();
            _tracks.Dispose();
            _lightning.Dispose();
            _mrms.Dispose();
            _future.Dispose();
            _outlooks.Dispose();
            _geocoder.Dispose();
            _storms.Dispose();
            _liveFeed.Dispose();
            _playback.Dispose();
            _panes.Dispose();
            _mapView.Dispose();
        };
    }

    // ---- messaging: transient status, persistent errors, determinate progress ----

    /// <summary>Transient status. Overwritten freely — this is the running commentary.</summary>
    private void Report(string text) => StatusText.Text = text;

    /// <summary>
    /// A failure the user needs to see. Errors used to share the status line with the
    /// sweep readout, which overwrote them inside a second; these persist until dismissed.
    /// </summary>
    public void ReportError(string text)
    {
        ErrorText.Text = text;
        ErrorBar.Visibility = Visibility.Visible;
        Serilog.Log.Warning("UI error surfaced: {Text}", text);
    }

    private void DismissError_Click(object sender, RoutedEventArgs e) =>
        ErrorBar.Visibility = Visibility.Collapsed;

    /// <summary>0–1 shows the strip; anything negative hides it.</summary>
    private void ShowProgress(double value)
    {
        if (value < 0)
        {
            TaskProgress.Visibility = Visibility.Collapsed;
            return;
        }
        TaskProgress.Visibility = Visibility.Visible;
        TaskProgress.IsIndeterminate = false;
        TaskProgress.Value = Math.Clamp(value, 0, 1);
    }

    private void ShowBusy(bool busy)
    {
        TaskProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TaskProgress.IsIndeterminate = busy;
    }

    // ---- keyboard ----

    /// <summary>
    /// Controls that own their keystrokes. Without this the search box was also a product
    /// switcher — typing "Vail" selected velocity — and arrow keys in the site list moved
    /// the tilt as well as the selection.
    /// </summary>
    private static bool OwnsKeys(IInputElement? focused) =>
        focused is TextBox or ComboBox or DatePicker or Slider or
                   System.Windows.Controls.Primitives.DatePickerTextBox;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            InfoWindow.ShowShortcuts(this);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ErrorBar.Visibility == Visibility.Visible)
        {
            ErrorBar.Visibility = Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        var focused = Keyboard.FocusedElement;
        if (OwnsKeys(focused)) return;
        // The map's child window already raised KeyPressed for this key.
        if (focused is D3DHostControl) return;

        if (_vm.Tool == MapTool.Draw && _drawing.OnKey(KeyInterop.VirtualKeyFromKey(e.Key)))
        {
            e.Handled = true;
            return;
        }

        if (_radar.OnKey(KeyInterop.VirtualKeyFromKey(e.Key)))
            e.Handled = true;
    }

    // ---- product and tilt ----

    /// <summary>Redraw the product bar from the controller, so keyboard and mouse agree.</summary>
    private void SyncProductBar()
    {
        // The segments' IsChecked is bound to the view model, so writing the selection
        // here echoes straight back through Checked/Unchecked.
        _suppressMomentEvents = true;
        _vm.SyncMoments(_radar.AvailableMoments, _radar.CurrentMoment);
        _suppressMomentEvents = false;

        var elevations = _radar.ElevationsForCurrentMoment;
        _vm.SyncTilts(elevations, _radar.CutPosition);
        TiltCombo.SelectedItem = _vm.SelectedTilt;
        TiltCombo.IsEnabled = elevations.Count > 0;
        TiltUpButton.IsEnabled = _radar.CutPosition < elevations.Count - 1;
        TiltDownButton.IsEnabled = _radar.CutPosition > 0;
        TiltCountText.Text = elevations.Count > 0
            ? $"{_radar.CutPosition + 1} of {elevations.Count}"
            : "";
        EmptyState.Visibility = _radar.DisplayedSweep is null && !_vm.IsForecast
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool _suppressMomentEvents;

    private void MomentSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressMomentEvents) return;
        if (sender is FrameworkElement { Tag: MomentOption option })
            _radar.SelectMoment(option.Moment);
        SyncProductBar();
    }

    /// <summary>A product is always showing, so the armed segment cannot be un-armed.</summary>
    private void MomentSegment_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressMomentEvents) return;
        if (sender is ToggleButton { Tag: MomentOption option } button &&
            option.Moment == _radar.CurrentMoment)
            button.IsChecked = true;
    }

    private void TiltCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.SuppressTiltEcho) return;
        if (TiltCombo.SelectedItem is TiltOption tilt)
            _radar.SelectCut(tilt.Position);
    }

    private void TiltUp_Click(object sender, RoutedEventArgs e) => _radar.StepCut(+1);

    private void TiltDown_Click(object sender, RoutedEventArgs e) => _radar.StepCut(-1);

    // ---- data mode: one switcher owns the time bar ----

    // Checked rather than Click, so the segments answer to assistive technology and to
    // keyboard activation the same way they answer to the mouse.
    private void ModeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressModeEvents) return;
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<DataMode>(tag, out var mode))
            ApplyMode(mode);
    }

    /// <summary>Un-checking the armed segment would leave no mode chosen; put it back.</summary>
    private void ModeButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressModeEvents) return;
        if (sender is ToggleButton { Tag: string tag } button &&
            Enum.TryParse<DataMode>(tag, out var mode) && _vm.Mode == mode)
            button.IsChecked = true;
    }

    /// <summary>
    /// Switching modes used to be a toggle with silent side effects — turning Live on
    /// quietly disabled the slider, and loading a day left a forecast raster on screen.
    /// One entry point makes the exclusivity explicit and reversible.
    /// </summary>
    private void ApplyMode(DataMode mode, bool initial = false)
    {
        if (!initial && _vm.Mode == mode) return;
        var previous = _vm.Mode;
        _vm.Mode = mode;

        _suppressModeEvents = true;
        LiveModeButton.IsChecked = mode == DataMode.Live;
        ArchiveModeButton.IsChecked = mode == DataMode.Archive;
        ForecastModeButton.IsChecked = mode == DataMode.Forecast;
        _suppressModeEvents = false;

        SyncPaneContext();
        LiveTransport.Visibility = mode == DataMode.Live ? Visibility.Visible : Visibility.Collapsed;
        ArchiveTransport.Visibility = mode == DataMode.Archive ? Visibility.Visible : Visibility.Collapsed;
        ForecastTransport.Visibility = mode == DataMode.Forecast ? Visibility.Visible : Visibility.Collapsed;

        if (initial) return;

        // Leaving a mode takes its data with it, so nothing stale is left under the new one.
        if (previous == DataMode.Live)
            _ = _liveFeed.StopAsync();
        if (previous == DataMode.Forecast)
            FutureClear_Click(this, new RoutedEventArgs());
        if (previous == DataMode.Archive)
            _playback.PauseLoop();

        switch (mode)
        {
            case DataMode.Live:
                StartLiveAsync();
                break;
            case DataMode.Archive:
                // Default the day without letting the picker's own handler fire — this
                // path loads it explicitly a line later.
                _suppressDayEvents = true;
                DayPicker.SelectedDate ??= DateTime.UtcNow.Date;
                _suppressDayEvents = false;
                _ = LoadSelectedDayAsync();
                break;
            case DataMode.Forecast:
                // The forecast raster and the MRMS composite occupy the same layer under
                // the radar, so one has to yield.
                if (FilterMrms.IsChecked == true) FilterMrms.IsChecked = false;
                Report("Forecast: load the HRRR run to see the next six hours.");
                break;
        }
    }

    private async void StartLiveAsync()
    {
        if (SiteCombo.SelectedItem is not RadarSite site)
        {
            ReportError("Pick a radar site before starting the live feed.");
            ApplyMode(DataMode.Archive);
            return;
        }
        LiveStateText.Text = $"Connecting to {site.Icao}…";
        _mapView.Camera.MoveTo(site.LatDeg, site.LonDeg, 250);
        await _liveFeed.StartAsync(site.Icao);
    }

    // ---- map tools ----

    private bool _suppressToolEvents;

    private void ToolButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressToolEvents) return;
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse<MapTool>(tag, out var tool))
            return;
        _vm.Tool = tool;
        SyncToolButtons();
    }

    /// <summary>Radio behaviour: the armed tool cannot be disarmed into no tool at all.</summary>
    private void ToolButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressToolEvents) return;
        if (sender is ToggleButton { Tag: string tag } button &&
            Enum.TryParse<MapTool>(tag, out var tool) && _vm.Tool == tool)
            button.IsChecked = true;
    }

    private void SyncToolButtons()
    {
        _suppressToolEvents = true;
        InspectTool.IsChecked = _vm.Tool == MapTool.Inspect;
        MeasureTool.IsChecked = _vm.Tool == MapTool.Measure;
        CrossSectionTool.IsChecked = _vm.Tool == MapTool.CrossSection;
        SetHomeTool.IsChecked = _vm.Tool == MapTool.SetHome;
        DrawTool.IsChecked = _vm.Tool == MapTool.Draw;
        _suppressToolEvents = false;

        CrossSectionPanel.Visibility = _vm.Tool == MapTool.CrossSection
            ? Visibility.Visible
            : Visibility.Collapsed;
        DrawingPanel.Visibility = _vm.Tool == MapTool.Draw
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Arming the tool is not a change to the drawing, so nothing else would have got
        // round to greying out what cannot be done on an empty one.
        SyncDrawingButtons();
        if (_vm.Tool == MapTool.Draw) _drawing.RefreshHint();
        Report(MainViewModel.ToolHint(_vm.Tool));
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
        if (!_vm.IsLive)
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

    private async Task LoadStartupAsync()
    {
        // A file passed on the command line bypasses everything else.
        string? path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (path is not null && File.Exists(path))
        {
            Report($"Decoding {Path.GetFileName(path)}…");
            ShowBusy(true);
            try
            {
                var volume = await Task.Run(() => ArchiveFile.DecodeFile(path));
                SiteCombo.SelectedItem = RadarSites.ByIcao(volume.SiteId) ?? SiteCombo.SelectedItem;
                _mapView.Camera.MoveTo(volume.LatDeg, volume.LonDeg, 250);
                _panes.ShowVolume(volume);
            }
            catch (Exception ex)
            {
                ReportError($"Could not decode {Path.GetFileName(path)}: {ex.Message}");
            }
            finally
            {
                ShowBusy(false);
            }
            return;
        }

        // With a home set, open on its radar, live. Otherwise stay on the national view
        // with the mosaic on — a first screen that is about the weather now, rather than
        // a hard-coded storm from 2013.
        if (_settings.HomeLatDeg is { } lat && _settings.HomeLonDeg is { } lon)
        {
            var site = RadarSites.Nearest(lat, lon);
            SiteCombo.SelectedItem = RadarSites.ByIcao(site.Icao);
            _mapView.Camera.MoveTo(lat, lon, 250);
            ApplyMode(DataMode.Live);
            return;
        }

        SiteCombo.SelectedItem ??= RadarSites.ByIcao("KTLX");
        FilterMosaic.IsChecked = true;
        Report("Pick a radar site, search for a place, or press 🎯 for the heaviest weather in the country.");
    }

    private async Task LoadSelectedDayAsync()
    {
        if (SiteCombo.SelectedItem is not RadarSite site || DayPicker.SelectedDate is not { } day)
            return;
        ShowBusy(true);
        try
        {
            await _playback.LoadDayAsync(site.Icao, DateOnly.FromDateTime(day));
        }
        finally
        {
            ShowBusy(false);
        }
    }

    private void UpdateSliderLabel()
    {
        int index = (int)TimeSlider.Value;
        TimeSliderLabel.Text = index >= 0 && index < _playback.DayVolumes.Count
            ? $"{_playback.DayVolumes[index].TimeUtc:HH:mm:ss}Z  ({index + 1}/{_playback.DayVolumes.Count})"
            : "—";
    }

    /// <summary>Keep the scrub handle under the playing loop, so the timeline stays honest.</summary>
    private void SyncSliderToLoop(int frameIndex)
    {
        int index = _playback.LoopWindowStart + frameIndex;
        if (index < 0 || index > TimeSlider.Maximum) return;
        _suppressSliderEvents = true;
        TimeSlider.Value = index;
        _suppressSliderEvents = false;
        UpdateSliderLabel();
    }

    private async void DayPicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDayEvents || !IsLoaded || !_vm.IsArchive) return;
        await LoadSelectedDayAsync();
    }

    private async void SiteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SiteCombo.SelectedItem is not RadarSite site) return;
        _mapView.SetSelectedMarker(site.LatDeg, site.LonDeg);
        if (!IsLoaded) return;

        // Follow the selection. Only live mode used to move the camera, and the archive
        // path only on the very first load — so picking a new site later left the map
        // sitting over the old one while the data quietly changed underneath.
        _mapView.Camera.MoveTo(site.LatDeg, site.LonDeg, 250);

        if (_vm.IsLive)
        {
            StartLiveAsync();
        }
        else if (_vm.IsArchive && DayPicker.SelectedDate is not null)
        {
            await LoadSelectedDayAsync();
        }
        if (StormsToggle.IsChecked == true && StormWatchSite() is { } watch)
            _storms.Enable(watch.Icao);
    }

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
            _playback.PauseLoop();
        else
            await _playback.PlayAsync();
    }

    private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _playback?.SetSpeed(SpeedCombo.SelectedIndex switch { 0 => 2, 2 => 8, _ => 4 });

    /// <summary>
    /// Tell the panes what the app is showing, so any pinned to their own site can match
    /// it. Called after every change to mode, day or scrub position — the three things
    /// that move the app through time.
    /// </summary>
    private void SyncPaneContext()
    {
        var day = DayPicker.SelectedDate is { } picked
            ? DateOnly.FromDateTime(picked)
            : DateOnly.FromDateTime(DateTime.UtcNow);

        // In archive mode the target is the scan on screen; live means "now".
        var target = _vm.IsArchive && _radar.DisplayedSweepTimeUtc is { } scan
            ? scan
            : DateTime.UtcNow;

        _panes.NotifyContext(_vm.Mode, day, target);
    }

    /// <summary>Merge warning polygons, storm features, and the measure line into one overlay.</summary>
    private void ComposeOverlay()
    {
        var labels = new List<MapView.MapLabel>(_storms.Labels);
        if (_outlooks.Labels is { Count: > 0 } outlookLabels)
            labels.AddRange(outlookLabels);
        if (_placefiles.Labels is { Count: > 0 } placefileLabels)
            labels.AddRange(placefileLabels);
        if (_drawing.Labels is { Count: > 0 } drawnLabels)
            labels.AddRange(drawnLabels);
        _mapView.SetLabels(labels);

        OverlayGeometry?[] sources =
        [
            _outlooks.Geometry, _warnings.Geometry, _placefiles.Geometry,
            _storms.Geometry, _lightning.Geometry, _homeGeometry, _measureGeometry,
            _drawing.Geometry,
        ];
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
        if (_settings.HomeLatDeg is { } lat && _settings.HomeLonDeg is { } lon)
            return RadarSites.Nearest(lat, lon);
        return SiteCombo.SelectedItem as RadarSite;
    }

    /// <summary>Home is set: make sure the storm layer is watching its nearest radar,
    /// so track alerts work without a manual toggle.</summary>
    private void EnsureStormWatchForHome()
    {
        if (_settings.HomeLatDeg is null || StormWatchSite() is not { } site) return;
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

    private void StormsToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _storms.Disable();
        StormNoteText.Text = "";
    }

    /// <summary>
    /// The storm-structure product (NSS) stopped being distributed around 2021, so on live
    /// data the cell labels can only ever show an ID. Say so rather than letting the user
    /// conclude the decoder is broken.
    /// </summary>
    private void UpdateStormNote(IReadOnlyList<TrackedStorm> storms)
    {
        StormNoteText.Text = storms.Count > 0 && storms.All(s => s.MaxDbz is null)
            ? "Labels show cell IDs only — the NWS stopped distributing the storm-structure "
            + "product (max dBZ, VIL, echo top) around 2021."
            : "";
    }

    // ---- click routing: whichever tool is armed decides ----

    private void RouteMapClick(int x, int y)
    {
        if (_stormPopup is not null) _stormPopup.IsOpen = false;
        _stormPopup = null;

        if (_vm.Tool == MapTool.Draw)
        {
            _drawing.OnClick(x, y);
            return;
        }

        var (lat, lon) = _mapView.ScreenToLatLon(x, y);
        if (_vm.Tool == MapTool.SetHome)
        {
            _vm.Tool = MapTool.Inspect;
            SyncToolButtons();
            _settings.HomeLatDeg = lat;
            _settings.HomeLonDeg = lon;
            _settings.Save();
            _threats.Configure(lat, lon, _settings.AlertRadiusKm);
            EnsureStormWatchForHome();
            RebuildHomeGeometry();
            ComposeOverlay();
            Report($"Home set to {lat:F3}, {lon:F3} — proximity alerts armed, " +
                   $"storm watch on {StormWatchSite()?.Icao}.");
            return;
        }

        if (_storms.HitTest(lat, lon) is { } storm)
        {
            ShowStormPopup(storm, x, y);
            return;
        }

        _warnings.HandleClick(x, y);
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
            Placement = PlacementMode.Relative,
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
        if (_settings.HomeLatDeg is not { } lat || _settings.HomeLonDeg is not { } lon)
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
        // The marker itself is a symbol, so it is sized in pixels, not metres.
        double s = 6 * _mapView.Camera.Snapshot().MetersPerPixel;
        geometry.FillTriangles.Add((centre.X - s, centre.Y - s, color));
        geometry.FillTriangles.Add((centre.X, centre.Y + s, color));
        geometry.FillTriangles.Add((centre.X + s, centre.Y - s, color));
        _homeGeometry = geometry;
    }

    private void OnThreat(Threat threat)
    {
        System.Media.SystemSounds.Exclamation.Play();
        Report($"⚠ {threat.Title}");

        // A toast inside the window is invisible when the window is not. Always send a
        // tray notification too — this is the case proximity alerts exist for.
        _tray.Notify(threat.Title, threat.Detail, threat.IsTornado);

        if (_toast is not null) _toast.IsOpen = false;
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
            Placement = PlacementMode.Relative,
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

    // ---- layers panel ----

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is not null)
            _mapView.RadarOpacity = (float)(e.NewValue / 100.0);
    }

    private void SmoothSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is not null)
            _mapView.RadarSmoothing = (float)(e.NewValue / 100.0);
    }

    private void MosaicFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_mapView is null) return;
        _mapView.MosaicEnabled = FilterMosaic.IsChecked == true;
        if (FilterMosaic.IsChecked == true && FilterMrms?.IsChecked == true)
            FilterMrms.IsChecked = false;
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

    private void SatelliteOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is not null)
            _mapView.SatelliteOpacity = (float)(e.NewValue / 100.0);
    }

    private void SiteMarkers_Changed(object sender, RoutedEventArgs e)
    {
        if (_mapView is null || _settings is null) return;
        _settings.ShowSiteMarkers = FilterSites.IsChecked == true;
        _mapView.MarkersEnabled = _settings.ShowSiteMarkers;
        _settings.Save();
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

    /// <summary>
    /// Average the tracked cells into one motion vector for storm-relative velocity.
    /// Averaging the components rather than the bearings avoids the wrap-around at north.
    /// </summary>
    private void UpdateStormMotion(IReadOnlyList<TrackedStorm> storms)
    {
        var moving = storms.Where(s => s.SpeedKmh > 3).ToList();
        if (moving.Count == 0) return;

        double u = moving.Average(s => s.SpeedKmh * Math.Sin(s.BearingDeg * Math.PI / 180.0));
        double v = moving.Average(s => s.SpeedKmh * Math.Cos(s.BearingDeg * Math.PI / 180.0));
        double speed = Math.Sqrt(u * u + v * v);
        double bearing = (Math.Atan2(u, v) * 180.0 / Math.PI + 360.0) % 360.0;

        _radar.StormMotion = (speed, bearing);
        if (_radar.StormRelative) _radar.Refresh();
    }

    /// <summary>
    /// Unfolding rewrites measured values, so the panel says plainly whether it is doing
    /// anything: a cut with no Nyquist velocity in its header cannot be unfolded at all,
    /// and silently ignoring the checkbox would look like a bug.
    /// </summary>
    private void Dealias_Changed(object sender, RoutedEventArgs e)
    {
        if (_radar is null || FilterDealias is null) return;
        _radar.DealiasVelocity = FilterDealias.IsChecked == true;
        _radar.Refresh();

        DealiasNoteText.Text = FilterDealias.IsChecked != true
            ? ""
            : _radar.CurrentMoment != Moment.Velocity
                ? "Applies to velocity — switch to VEL to see it."
                : _radar.DisplayedSweep?.NyquistMs is { } nyquist
                    ? $"Unfolding against ±{nyquist:F1} m/s."
                    : "This cut reports no Nyquist velocity, so nothing can be unfolded.";
    }

    private void StormRelative_Changed(object sender, RoutedEventArgs e)
    {
        if (_radar is null) return;
        _radar.StormRelative = FilterStormRelative.IsChecked == true;
        if (_radar.StormRelative && _radar.StormMotion.SpeedKmh <= 0)
            Report("Storm-relative needs a motion vector — turn on Track storms so cells are tracked.");
        _radar.Refresh();
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

    /// <summary>
    /// Native MRMS and the pre-rendered tile mosaic are the same field, so showing both
    /// just draws one over the other; picking either turns the other off.
    /// </summary>
    private void Mrms_Changed(object sender, RoutedEventArgs e)
    {
        if (_mrms is null || FilterMrms is null) return;
        if (FilterMrms.IsChecked == true)
        {
            if (FilterMosaic.IsChecked == true) FilterMosaic.IsChecked = false;
            if (_vm.IsForecast) ApplyMode(DataMode.Archive);
            MrmsNoteText.Text = "Fetching…";
            _mrms.Enable();
        }
        else
        {
            _mrms.Disable();
            MrmsNoteText.Text = "";
        }
    }

    private void MrmsOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mrms is not null) _mrms.Opacity = (float)(e.NewValue / 100.0);
    }

    private void Lightning_Changed(object sender, RoutedEventArgs e)
    {
        if (_lightning is null || FilterLightning is null) return;
        if (FilterLightning.IsChecked == true)
        {
            LightningNoteText.Text = "Fetching…";
            _lightning.Enable();
        }
        else
        {
            _lightning.Disable();
            LightningNoteText.Text = "";
        }
    }

    /// <summary>
    /// Keep the play button honest about how far back the loop reaches. It used to state a
    /// hardcoded thirty, which stopped being true the moment the span became a setting.
    /// </summary>
    private void SyncLoopTooltip()
    {
        if (PlayButton is null) return;
        int frames = _playback.LoopFrames;
        PlayButton.ToolTip =
            $"Loop the most recent {frames} volumes of the loaded day — roughly "
            + $"{frames * 5 / 60.0:0.#} hours at a five-minute scan. Change the span in Settings.";
    }

    // ---- drawing ----

    private void DrawKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent, before the rest of the panel exists.
        if (DrawTextBox is null) return;

        if (DrawKindCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<DrawingKind>(tag, out var kind))
            _drawing.Kind = kind;

        // The label box is only meaningful for labels, and an always-visible text field
        // invites typing into it while drawing a line.
        var show = _drawing.Kind == DrawingKind.Text ? Visibility.Visible : Visibility.Collapsed;
        DrawTextLabel.Visibility = show;
        DrawTextBox.Visibility = show;
        _drawing.RefreshHint();
        SyncDrawingButtons();
    }

    private void DrawColor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_drawing is null) return;
        if (DrawColorCombo.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            var parts = tag.Split(',');
            if (parts.Length == 3 &&
                byte.TryParse(parts[0], out byte r) &&
                byte.TryParse(parts[1], out byte g) &&
                byte.TryParse(parts[2], out byte b))
                _drawing.Color = new OpenWSR.Placefiles.PlaceColor(r, g, b);
        }
    }

    private void DrawWidth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_drawing is null) return;
        _drawing.WidthPx = (float)e.NewValue;
    }

    private void DrawText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_drawing is null) return;
        _drawing.PendingText = DrawTextBox.Text;
        _drawing.RefreshHint();
        SyncDrawingButtons();
    }

    private void DrawFinish_Click(object sender, RoutedEventArgs e) => _drawing.Finish();

    /// <summary>Backspace's job: the point being placed if there is one, else the last shape.</summary>
    private void DrawUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_drawing.IsDrawing) _drawing.UndoPoint();
        else _drawing.UndoShape();
    }

    private void DrawClear_Click(object sender, RoutedEventArgs e)
    {
        if (!_drawing.HasContent) return;
        // Losing an annotation is not recoverable, so it is worth one question.
        var answer = MessageBox.Show(
            this,
            $"Delete all {_drawing.ShapeCount} shapes? Save first if you want to keep them.",
            "Clear drawing", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        _drawing.Clear();
        Report("Drawing cleared.");
    }

    private void DrawSave_Click(object sender, RoutedEventArgs e)
    {
        if (!_drawing.HasContent)
        {
            Report("Nothing drawn yet.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save drawing",
            Filter = "Placefile (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            // Somewhere the user can find it again, rather than wherever the process
            // happens to have been started from.
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = $"drawing-{DateTime.Now:yyyyMMdd-HHmm}.txt",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _drawing.Save(dialog.FileName);
            Report($"Saved {_drawing.ShapeCount} shapes to {System.IO.Path.GetFileName(dialog.FileName)} " +
                   "— it is a placefile, so it opens in GR too.");
        }
        catch (Exception ex)
        {
            Report($"Could not save the drawing: {ex.Message}");
        }
    }

    private void DrawOpen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a drawing or placefile",
            Filter = "Placefile (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            int added = _drawing.Load(dialog.FileName);
            Report(added == 0
                ? "That file had no shapes this can draw."
                : $"Added {added} shapes from {System.IO.Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex)
        {
            Report($"Could not open that drawing: {ex.Message}");
        }
    }

    /// <summary>Grey out what cannot be done yet, rather than letting it fail on click.</summary>
    private void SyncDrawingButtons()
    {
        if (DrawFinishButton is null) return;
        DrawFinishButton.IsEnabled = _drawing.IsDrawing;
        DrawUndoButton.IsEnabled = _drawing.IsDrawing || _drawing.HasContent;
        DrawSaveButton.IsEnabled = _drawing.HasContent;
        DrawClearButton.IsEnabled = _drawing.HasContent;
    }

    // ---- wind profile ----

    /// <summary>One row of the profile, shaped for the panel's template.</summary>
    private sealed record WindRow(string Height, string Wind, System.Windows.Media.Geometry Barb);

    private void WindProfile_Changed(object sender, RoutedEventArgs e)
    {
        if (WindProfilePanel is null) return;
        bool on = WindProfileToggle.IsChecked == true;
        WindProfilePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) RebuildWindProfile();
    }

    /// <summary>
    /// Fit the profile from the volume on screen. Sampling costs real time on a full
    /// volume, so it runs off the UI thread and only while the panel is open.
    /// </summary>
    private async void RebuildWindProfile()
    {
        if (WindProfileToggle.IsChecked != true) return;

        var sweeps = _radar.AllVelocitySweeps();
        if (sweeps.Count == 0)
        {
            WindProfileList.ItemsSource = null;
            WindProfileNote.Text = "No velocity in this volume.";
            return;
        }

        WindProfileNote.Text = "Fitting…";
        var levels = await Task.Run(() => VadProfile.Compute(sweeps));

        if (levels.Count == 0)
        {
            WindProfileList.ItemsSource = null;
            WindProfileNote.Text =
                "No level had enough echo around the radar to fit a wind. Clear air often will not.";
            return;
        }

        // Top of the profile first, the way a sounding is read.
        WindProfileList.ItemsSource = levels
            .OrderByDescending(l => l.AltitudeM)
            .Select(l => new WindRow(
                Units.System == UnitSystem.Metric
                    ? $"{l.AltitudeM / 1000:F1} km"
                    : $"{l.AltitudeM * 3.28084 / 1000:F1} kft",
                $"{l.DirectionDeg:F0}° {l.SpeedKnots:F0}kt",
                WindBarbs.Build(l.SpeedKnots, l.DirectionDeg)))
            .ToList();

        var lowest = levels[0];
        WindProfileNote.Text =
            $"{levels.Count} levels to {levels[^1].AltitudeM / 1000:F1} km · "
            + $"surface flow {lowest.DirectionDeg:F0}° at {lowest.SpeedKnots:F0} kt";
    }

    private void SymbolKeyButton_Click(object sender, RoutedEventArgs e) =>
        InfoWindow.ShowSymbolKey(this);

    // ---- rotation tracks ----

    private async void TracksBuild_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsArchive)
        {
            ReportError("Rotation tracks accumulate archived scans — switch to ARCHIVE and load a day.");
            return;
        }
        TracksBuildButton.IsEnabled = false;
        try
        {
            await _tracks.BuildAsync(_playback.DayVolumes, (int)TimeSlider.Value);
            TracksClearButton.IsEnabled = _tracks.IsLoaded;
        }
        finally
        {
            TracksBuildButton.IsEnabled = true;
        }
    }

    private void TracksClear_Click(object sender, RoutedEventArgs e)
    {
        _tracks.Clear();
        TracksClearButton.IsEnabled = false;
        TracksStatusText.Text = "Cleared.";
    }

    private void TracksOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_tracks is not null) _tracks.Opacity = (float)(e.NewValue / 100.0);
    }

    // ---- vertical cross-section ----

    /// <summary>
    /// Turn the measuring line into a slice. Sampling the whole volume takes a moment,
    /// so it runs off the UI thread.
    /// </summary>
    private async void BuildCrossSection(double lat1, double lon1, double lat2, double lon2)
    {
        var sweeps = _radar.SweepsForCurrentMoment();
        if (sweeps.Count == 0)
        {
            CrossSectionTitle.Text = "Cross-section — no volume loaded";
            return;
        }
        if (GeoMath.DistanceM(lat1, lon1, lat2, lon2) < 2000)
            return; // a click, not a line

        CrossSectionTitle.Text = "Cross-section — sampling…";
        try
        {
            var section = await Task.Run(() =>
                CrossSection.Build(sweeps, lat1, lon1, lat2, lon2));

            CrossSectionView.Source = CrossSectionImage.Render(section, _radar.CurrentTable);
            CrossSectionTitle.Text =
                $"Cross-section — {_radar.CurrentMoment}, {Units.Distance(section.LengthKm)} long, " +
                $"{section.ElevationsUsed.Count} cuts from {section.ElevationsUsed[0]:F1}° " +
                $"to {section.ElevationsUsed[^1]:F1}°";
            CrossSectionStart.Text = $"{lat1:F2}, {lon1:F2}";
            CrossSectionEnd.Text = $"{lat2:F2}, {lon2:F2}";
        }
        catch (Exception ex)
        {
            CrossSectionTitle.Text = $"Cross-section failed: {ex.Message}";
        }
    }

    // ---- capture ----

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        // The GIF option used to appear only while a loop was playing — and taking it
        // stopped the loop, discarding the very frames it was about to record. Any loaded
        // day can be recorded now; the frames are built on demand.
        bool canRecord = _playback.LoopGeometryCount > 0 || _playback.CanBuildLoop;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save map",
            FileName = $"openwsr-{DateTime.Now:yyyyMMdd-HHmmss}",
            Filter = canRecord
                ? "PNG image (*.png)|*.png|Animated GIF of the loop (*.gif)|*.gif"
                : "PNG image (*.png)|*.png",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (dialog.FileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                _ = SaveLoopGifAsync(dialog.FileName);
            }
            else
            {
                var frame = _mapView.CaptureFrame();
                if (frame is null)
                {
                    ReportError("Could not read the map surface. Try again once the map has drawn a frame.");
                    return;
                }
                GifWriter.SavePng(dialog.FileName, frame.Value.Bgra, frame.Value.Width, frame.Value.Height);
                Report($"Saved {Path.GetFileName(dialog.FileName)}");
            }
        }
        catch (Exception ex)
        {
            ReportError($"Could not save {Path.GetFileName(dialog.FileName)}: {ex.Message}");
        }
    }

    /// <summary>
    /// Step the loop frame by frame, grabbing the rendered map each time. Capturing what
    /// is actually on screen means the GIF carries every layer, not just the radar.
    /// </summary>
    private async Task SaveLoopGifAsync(string path)
    {
        bool wasPlaying = _playback.IsPlaying;
        _playback.PauseLoop(); // pause, not stop — stopping would discard the frames

        Report("Preparing the loop…");
        if (!await _playback.EnsureLoopAsync())
        {
            ReportError("There are no loop frames to record. Load an archive day first.");
            return;
        }

        var frames = new List<(byte[] Bgra, int Width, int Height)>();
        int count = _playback.LoopGeometryCount;
        for (int i = 0; i < count; i++)
        {
            _playback.ShowLoopFrame(i);
            await Task.Delay(140); // let the render thread present the new frame
            if (_mapView.CaptureFrame() is { } frame)
                frames.Add(frame);
            Report($"Recording the loop… {i + 1}/{count}");
            ShowProgress((i + 1) / (double)count);
        }
        ShowProgress(-1);

        if (frames.Count == 0)
        {
            ReportError("Nothing was captured — the map surface did not return a frame.");
            return;
        }
        try
        {
            await Task.Run(() => GifWriter.Save(path, frames, delayCentiseconds: 25));
            Report($"Saved {Path.GetFileName(path)} ({frames.Count} frames)");
        }
        catch (Exception ex)
        {
            ReportError($"Could not write {Path.GetFileName(path)}: {ex.Message}");
            return;
        }
        if (wasPlaying) await _playback.PlayAsync();
    }

    // ---- placefiles ----

    private async void PlacefileAddUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = PlacefilePrompt.Ask(this);
        if (string.IsNullOrWhiteSpace(url)) return;
        await AddPlacefileAsync(url.Trim());
    }

    private async void PlacefileAddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open placefile",
            Filter = "Placefiles (*.txt;*.php)|*.txt;*.php|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        await AddPlacefileAsync(dialog.FileName);
    }

    private async Task AddPlacefileAsync(string source)
    {
        await _placefiles.AddAsync(source);
        if (_placefiles.Files.Any(f => f.Source == source) && !_settings.Placefiles.Contains(source))
        {
            _settings.Placefiles.Add(source);
            _settings.Save();
        }
    }

    private void PlacefileToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: LoadedPlacefile file } toggle)
            _placefiles.SetEnabled(file, toggle.IsChecked == true);
    }

    private void PlacefileRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LoadedPlacefile file }) return;
        _placefiles.Remove(file);
        if (_settings.Placefiles.Remove(file.Source))
            _settings.Save();
    }

    // ---- future radar ----

    private async void FutureLoad_Click(object sender, RoutedEventArgs e)
    {
        FutureLoadButton.IsEnabled = false;
        ShowBusy(true);
        try
        {
            await _future.LoadAsync();
        }
        finally
        {
            FutureLoadButton.IsEnabled = true;
            ShowBusy(false);
        }
    }

    private void FuturePrev_Click(object sender, RoutedEventArgs e) => _future.Step(-1);

    private void FutureNext_Click(object sender, RoutedEventArgs e) => _future.Step(1);

    private void FuturePlay_Click(object sender, RoutedEventArgs e)
    {
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
        _future.Clear();
        FuturePlayButton.Content = "▶";
        FutureLabel.Text = "Not loaded";
        foreach (var b in new[] { FuturePrevButton, FuturePlayButton, FutureNextButton, FutureClearButton })
            b.IsEnabled = false;
    }

    // ---- rail ----

    private void LayersToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (LayersPanel is not null)
            LayersPanel.Visibility = LayersToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LinkToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_panes is not null)
            _panes.LinkedPan = LinkToggle.IsChecked == true;
    }

    /// <summary>Rail button cycles 1 → 2 → 4 panes, so the rail needs no dropdown.</summary>
    private void PaneButton_Click(object sender, RoutedEventArgs e)
    {
        _paneCount = _paneCount switch { 1 => 2, 2 => 4, _ => 1 };
        _panes.SetPaneCount(_paneCount);
        PaneButton.Content = _paneCount switch { 1 => "◱", 2 => "◫", _ => "⊞" };
        PaneButton.ToolTip = $"Map panes: {_paneCount}";
        LinkToggle.IsEnabled = _paneCount > 1;
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
            Report($"Palette applied to {_radar.CurrentMoment}: {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex)
        {
            ReportError($"Could not read {Path.GetFileName(dialog.FileName)}: {ex.Message}");
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
        ShowProgress(0);
        try
        {
            Report("Scanning every radar site for the heaviest precipitation…");
            var hotspot = await NationalStormScan.FindHeaviestAsync((done, total) =>
                Dispatcher.BeginInvoke(() =>
                {
                    Report($"Scanning storm structure… {done}/{total} sites");
                    ShowProgress(done / (double)Math.Max(total, 1));
                }));
            if (hotspot is null)
            {
                Report("No fresh storm cells anywhere in the USA — remarkably quiet.");
                return;
            }

            SiteCombo.SelectedItem = RadarSites.ByIcao(hotspot.Site.Icao);
            ApplyMode(DataMode.Live);
            StormsToggle.IsChecked = true;
            _storms.Enable(hotspot.Site.Icao); // follow the hotspot (overrides home watch for now)
            _mapView.Camera.MoveTo(hotspot.LatDeg, hotspot.LonDeg, 150);

            Report($"🎯 Hotspot: VIL {hotspot.MaxVilKgM2:F0} kg/m² near {hotspot.Site.Icao} " +
                   $"({hotspot.Site.Name}, {hotspot.Site.State}), {Units.Distance(hotspot.RangeKm)} out — " +
                   $"as of {hotspot.ProductTimeUtc:HH:mm}Z");
        }
        catch (Exception ex)
        {
            ReportError($"The hotspot scan failed: {ex.Message}");
        }
        finally
        {
            HotspotButton.IsEnabled = true;
            ShowProgress(-1);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        Units.System = _settings.Units;
        _playback.LoopFrames = _settings.LoopFrames;
        SyncLoopTooltip();
        _threats.Configure(_settings.HomeLatDeg, _settings.HomeLonDeg, _settings.AlertRadiusKm);
        RebuildHomeGeometry();
        ComposeOverlay();
        _radar.Refresh();

        if (dialog.WantsHomePicker)
        {
            _vm.Tool = MapTool.SetHome;
            SyncToolButtons();
            return;
        }
        Report("Settings saved. Basemap changes take effect next launch.");
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e) => InfoWindow.ShowShortcuts(this);

    private void AboutButton_Click(object sender, RoutedEventArgs e) =>
        InfoWindow.ShowAbout(this, GetType().Assembly.GetName().Version?.ToString(3) ?? "dev");

    // ---- location search ----

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;

        SearchBox.IsEnabled = false;
        Report($"Looking up “{query}”…");
        try
        {
            var place = await _geocoder.SearchAsync(query);
            if (place is null)
            {
                Report($"No match for “{query}”. Try a city, a ZIP code, or lat,lon.");
                return;
            }
            var site = RadarSites.Nearest(place.LatDeg, place.LonDeg);
            double km = GeoMath.DistanceM(place.LatDeg, place.LonDeg, site.LatDeg, site.LonDeg) / 1000.0;
            SiteCombo.SelectedItem = RadarSites.ByIcao(site.Icao);
            _mapView.Camera.MoveTo(place.LatDeg, place.LonDeg, 220);
            Report($"{place.Name} — nearest radar {site.Icao} ({site.Name}), {Units.Distance(km)} away.");
            SearchBox.Clear();
        }
        catch (Exception ex)
        {
            // A geocoder failure used to reach the dispatcher and end the session.
            ReportError($"Could not look up “{query}”: {ex.Message}");
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
