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
    private readonly TdwrFeed _tdwr = new(new Level3Client());
    private readonly WarningsController _warnings;
    private readonly InspectorTools _inspector;
    private readonly StormOverlayController _storms;
    private readonly ThreatMonitor _threats = new();
    private SatelliteController? _satellite;
    private readonly OutlookOverlayController _outlooks;
    private readonly FutureRadarController _future;
    private readonly PlacefileController _placefiles;
    private readonly RotationTracksController _tracks;
    private readonly BoundariesController _boundaries;
    private readonly ShapeImportController _imports;
    private bool _reportedTileFailure;
    private readonly LightningController _lightning;
    private readonly MrmsController _mrms;
    private readonly MrmsController _hailField;
    private readonly TrayNotifier _tray;
    private readonly PaneManager _panes;
    private readonly Geocoder _geocoder;
    private readonly AppSettings _settings;
    private OverlayGeometry? _measureGeometry;
    private readonly DrawingController _drawing;
    private readonly VolumeController _volume;
    private OverlayGeometry? _homeGeometry;
    private double _homeBuiltAtMetresPerPixel;
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
        FitRestoreBoundsToScreen();
        MomentBar.ItemsSource = _vm.Moments;
        TiltCombo.ItemsSource = _vm.Tilts;
        ThreatList.ItemsSource = _vm.Threats;
        LinkToggle.Visibility = Visibility.Collapsed; // appears with the second pane

        var settings = _settings = AppSettings.Load();
        Units.System = settings.Units;
        _geocoder = new Geocoder(settings.UserAgent);
        var provider = settings.TileProvider switch
        {
            "maptiler" when !string.IsNullOrEmpty(settings.MapTilerKey) =>
                TileProvider.MapTiler(settings.MapTilerKey, settings.UserAgent),
            "carto-dark" => TileProvider.CartoDark(settings.UserAgent),
            "usgs-topo" => TileProvider.UsgsTopo(settings.UserAgent),
            _ => TileProvider.Osm(settings.UserAgent),
        };
        AttributionText.Text = provider.Name switch
        {
            "maptiler" => "© MapTiler © OpenStreetMap contributors",
            "carto-dark" => "© OpenStreetMap contributors © CARTO",
            "usgs-topo" => "USGS The National Map",
            _ => "© OpenStreetMap contributors",
        };

        _mapView = new MapView(provider);
        _mapView.Camera.MoveTo(39.0, -98.0, 6000); // continental US
        _mapView.SetMarkers(RadarSites.All.Select(s => (s.LatDeg, s.LonDeg, s.Icao)));
        _mapView.MarkersEnabled = settings.ShowSiteMarkers;
        FilterSites.IsChecked = settings.ShowSiteMarkers;
        FilterStates.IsChecked = settings.ShowStateLines;
        FilterCounties.IsChecked = settings.ShowCountyLines;
        MapHost.Child = new D3DHostControl(_mapView);

        _radar = new RadarDisplayController(_mapView);

        _volume = new VolumeController(_mapView, _radar);
        _volume.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));
        _volume.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));
        _volume.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            VolumeSummary.Text = _volume.Summary ?? "";
        });

        _radar.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));
        _radar.SelectionChanged += () => Dispatcher.BeginInvoke(() =>
        {
            SyncProductBar();
            RebuildWindProfile();
            if (_volume.IsActive) _ = _volume.RequestRebuildAsync();
        });

        _drawing = new DrawingController(_mapView);

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

        foreach (var site in RadarSites.All.OrderBy(s => s.Icao))
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

        _boundaries = new BoundariesController(_mapView, settings.UserAgent);
        _boundaries.GeometryChanged += () => Dispatcher.BeginInvoke(ComposeOverlay);
        _boundaries.StatusChanged += text => Dispatcher.BeginInvoke(() =>
        {
            Report(text);
            BoundariesStatusText.Text = text;
        });
        _boundaries.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));

        // The checkboxes were restored above, before this existed, so their handler no-opped.
        _boundaries.SetVisible(BoundarySet.States, settings.ShowStateLines);
        _boundaries.SetVisible(BoundarySet.Counties, settings.ShowCountyLines);

        _imports = new ShapeImportController(_mapView);
        _imports.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            ImportList.ItemsSource = null;
            ImportList.ItemsSource = _imports.Files;
            ComposeOverlay();
        });
        _imports.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));
        _imports.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));

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

        _satellite = new SatelliteController(_mapView);
        _satellite.StatusChanged += text => Dispatcher.BeginInvoke(() =>
        {
            Report(text);
            SatelliteNoteText.Text = text;
        });
        _satellite.ErrorRaised += text => Dispatcher.BeginInvoke(() =>
        {
            ReportError(text);
            SatelliteNoteText.Text = "Falling back to pre-rendered tiles.";
        });

        _mrms = new MrmsController(_mapView);
        _mrms.StatusChanged += text => Dispatcher.BeginInvoke(() =>
        {
            Report(text);
            MrmsNoteText.Text = text;
        });
        _mrms.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));

        _hailField = new MrmsController(_mapView, MrmsLayer.HailSize);
        _hailField.StatusChanged += text => Dispatcher.BeginInvoke(() => Report(text));
        _hailField.ErrorRaised += text => Dispatcher.BeginInvoke(() => ReportError(text));

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
        _threats.ThreatsChanged += list => Dispatcher.BeginInvoke(() => ShowThreatList(list));
        _drawing.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            ComposeOverlay();
            SyncDrawingButtons();
        });
        _drawing.HintChanged += text => Dispatcher.BeginInvoke(() => DrawHint.Text = text);

        _mapView.Clicked += RouteMapClick;

        ConfigureThreats();
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
            // A volume already in flight when the site changed belongs to the old radar, and
            // drawing it would put the previous site's picture under the new site's name.
            if (SiteCombo.SelectedItem is RadarSite selected &&
                !selected.Icao.Equals(volume.SiteId, StringComparison.OrdinalIgnoreCase))
                return;
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

            // A basemap whose tiles all fail leaves a blank map, which reads as "still
            // loading" for ever. Say so once.
            if (_mapView.BasemapFailure is { } tileFailure && !_reportedTileFailure)
            {
                _reportedTileFailure = true;
                ReportError($"Basemap tiles are not loading — {tileFailure}");
            }
            UpdateAgeIndicator();
            // Storm symbols are sized in screen pixels, so a zoom change means new geometry.
            _storms.NotifyViewChanged();
            _boundaries.NotifyViewChanged();
            _imports.NotifyViewChanged();
            _lightning.NotifyViewChanged();
            _mrms.NotifyViewChanged();
            _hailField.NotifyViewChanged();
            NotifyHomeViewChanged();
            // The alerts poll drops expired warnings once a minute; the list must not show
            // one that has already run out in the meantime as though it were still in force.
            _threats.ExpireStale(DateTimeOffset.UtcNow);
            PollTdwrIfDue();
        };
        _statusTimer.Start();

        ApplyMode(DataMode.Archive, initial: true);
        SyncProductBar();
        SyncToolButtons();

        Loaded += async (_, _) =>
        {
            SyncLoopTooltip();
            RestorePanelSections();
            foreach (var source in settings.Placefiles.ToList())
                await _placefiles.AddAsync(source);
            foreach (var path in settings.ImportedShapes.ToList())
            {
                if (System.IO.File.Exists(path)) await _imports.AddAsync(path);
                else settings.ImportedShapes.Remove(path);
            }
            await LoadStartupAsync();
            if (!settings.WelcomeShown)
            {
                settings.WelcomeShown = true;
                settings.Save();
                InfoWindow.ShowGuide(this);
            }
        };
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            _tray.Dispose();
            _placefiles.Dispose();
            _tracks.Dispose();
            _boundaries.Dispose();
            _lightning.Dispose();
            _mrms.Dispose();
            _hailField.Dispose();
            _satellite?.Dispose();
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
            InfoWindow.ShowGuide(this);
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
        FrameSite(site);

        if (site.IsTdwr)
        {
            await _liveFeed.StopAsync();   // the Level II chunk stream does not apply here
            await LoadTdwrAsync(site);
            return;
        }

        LiveStateText.Text = $"Connecting to {site.Icao}…";
        await _liveFeed.StartAsync(site.Icao);
    }

    /// <summary>
    /// Fetch and show a TDWR volume, assembled from its six Level III products.
    ///
    /// Polled rather than streamed: a WSR-88D publishes Level II in chunks as the antenna
    /// turns, which is what gives this app sub-scan latency, but a TDWR only reaches the
    /// public as finished Level III products. A minute is about their cadence.
    /// </summary>
    private async Task LoadTdwrAsync(RadarSite site)
    {
        int generation = ++_tdwrGeneration;
        _lastTdwrFetchUtc = DateTime.UtcNow;
        LiveStateText.Text = $"Fetching {site.Icao}…";
        Report($"{site.Icao}: fetching terminal radar products…");
        ShowBusy(true);
        try
        {
            var volume = await _tdwr.GetLatestAsync(site);
            if (generation != _tdwrGeneration) return;
            if (volume is null)
            {
                // Not an error. These run a hazardous-weather strategy and go quiet in clear
                // air, where a WSR-88D keeps sweeping a clear-air VCP.
                LiveStateText.Text = $"{site.Icao}: nothing published";
                Report($"{site.Icao} is not publishing right now — terminal radars go quiet " +
                       "in clear air. Try a site with weather over it.");
                return;
            }

            _panes.ShowVolume(volume);
            SyncPaneContext();
            double age = (DateTime.UtcNow - volume.StartTimeUtc).TotalMinutes;
            LiveStateText.Text =
                $"{site.Icao} {volume.StartTimeUtc:HH:mm:ss}Z, {volume.Sweeps.Count} products";
            Report($"{site.Icao}: {volume.Sweeps.Count} products, newest " +
                   $"{volume.StartTimeUtc:HH:mm:ss}Z ({age:F0} min old)");
        }
        catch (Exception ex)
        {
            if (generation != _tdwrGeneration) return;
            ReportError($"{site.Icao} fetch failed: {ex.Message}");
        }
        finally
        {
            if (generation == _tdwrGeneration) ShowBusy(false);
        }
    }

    private int _tdwrGeneration;
    private DateTime _lastTdwrFetchUtc = DateTime.MinValue;

    /// <summary>
    /// Keep a live TDWR current. It rides the half-second status tick rather than owning a
    /// timer, because it is a rate limit rather than a schedule: the products publish about
    /// once a minute and asking more often only spends requests.
    /// </summary>
    private void PollTdwrIfDue()
    {
        if (!_vm.IsLive || SiteCombo.SelectedItem is not RadarSite { IsTdwr: true } site) return;
        if (DateTime.UtcNow - _lastTdwrFetchUtc < TimeSpan.FromSeconds(60)) return;
        _lastTdwrFetchUtc = DateTime.UtcNow;
        _ = LoadTdwrAsync(site);
    }

    /// <summary>
    /// Where the camera belongs when we start looking at a site. Home wins over the tower
    /// whenever this is home's own radar: setting a home says which ground you care about,
    /// and a WSR-88D is routinely fifty miles from it, so centring on the tower pushes your
    /// own house out towards the edge of the view. Picking some other site deliberately is a
    /// different intent and still frames the tower.
    ///
    /// This is a function of the site rather than a sequence of moves because it has three
    /// callers that used to run in an order nobody could see: startup framed home, then
    /// ApplyMode started the live feed, which framed the tower straight over the top of it.
    /// Whoever moves the camera last now computes the same answer.
    /// </summary>
    private void FrameSite(RadarSite site)
    {
        if (_settings.Primary is { } primary
            && RadarSites.Nearest(primary.LatDeg, primary.LonDeg).Icao == site.Icao)
            _mapView.Camera.MoveTo(primary.LatDeg, primary.LonDeg, 250);
        else
            _mapView.Camera.MoveTo(site.LatDeg, site.LonDeg, 250);
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
        DrawTool.IsChecked = _vm.Tool == MapTool.Draw;
        // SetHome has no rail button — it is armed from Settings and disarms itself on the
        // next click — so every rail toggle is unchecked while it is the armed tool. That is
        // correct rather than a gap: the status line carries the hint, and leaving Inspect
        // lit would say a different tool was armed than the one that is.
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

        // With a home set, open on its radar, live, and centred on the house rather than the
        // tower — see FrameSite. Otherwise stay on the national view with the mosaic on: a
        // first screen that is about the weather now, rather than a hard-coded storm from 2013.
        if (_settings.Primary is { } primary)
        {
            var site = RadarSites.Nearest(primary.LatDeg, primary.LonDeg);
            SiteCombo.SelectedItem = RadarSites.ByIcao(site.Icao);
            FrameSite(site);
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
        FrameSite(site);

        if (site.IsTdwr)
        {
            // A TDWR publishes only Level III, so there is no archive to scrub and no volume
            // to run a forecast against. Switching to live is the honest response to picking
            // one rather than leaving the transport pointed at data that does not exist.
            //
            // Both branches end in StartLiveAsync because it is what stops the Level II chunk
            // stream. Loading the terminal volume without doing that leaves the previous
            // site's feed running, and its next volume lands on top of what was just drawn —
            // the site box says one radar and the picture is still the other one.
            if (!_vm.IsLive) ApplyMode(DataMode.Live);
            else StartLiveAsync();
        }
        else if (_vm.IsLive)
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
            _boundaries.Geometry, _imports.Geometry,
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

    /// <summary>
    /// Keep the restored (un-maximised) size inside the screen's working area.
    ///
    /// <c>Width</c> and <c>Height</c> in XAML are device-independent units, so the declared
    /// 1360x860 is 2040x1290 real pixels on a 150 % display — larger than a 1920x1200 screen.
    /// WPF does not clamp that, so the window opened bigger than the monitor with its bottom
    /// and right edges off it, which is where the bottom of the left rail was disappearing to.
    /// The work area rather than the full bounds, so the taskbar is not sat under either.
    /// </summary>
    private void FitRestoreBoundsToScreen()
    {
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width);
        Height = Math.Min(Height, work.Height);
    }

    /// <summary>The site the storm layer should watch: nearest to home when home is set,
    /// otherwise whatever is selected for browsing.</summary>
    private RadarSite? StormWatchSite()
    {
        if (_settings.Primary is { } primary)
            return RadarSites.Nearest(primary.LatDeg, primary.LonDeg);
        return SiteCombo.SelectedItem as RadarSite;
    }

    /// <summary>Home is set: make sure the storm layer is watching its nearest radar,
    /// so track alerts work without a manual toggle.</summary>
    private void EnsureStormWatchForHome()
    {
        if (_settings.Primary is null || StormWatchSite() is not { } site) return;
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
            // Moves the primary if there is one, adds the first otherwise. Picking on the map
            // is how you correct a place, not how you accumulate them — adding a new one every
            // click would turn a nudge into a list.
            var place = _settings.Primary;
            if (place is null)
            {
                place = new SavedLocation { Name = "Home", IsPrimary = true };
                _settings.Locations.Add(place);
            }
            place.LatDeg = lat;
            place.LonDeg = lon;
            place.Source = "map";
            place.AccuracyM = null;
            _settings.Save();
            ConfigureThreats();
            EnsureStormWatchForHome();
            RebuildHomeGeometry();
            ComposeOverlay();
            Report($"{place.Name} set to {lat:F3}, {lon:F3} — proximity alerts armed, " +
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

    /// <summary>
    /// What this cell's track does relative to home, in one line. Says the same three things
    /// the notification tiers do — coming for you, going by, or already leaving — because a
    /// popup that says "passes 22 mi from home" leaves the reader to work out which.
    /// </summary>
    private string ApproachLine(GeoMath.PathApproach approach)
    {
        if (approach.IsReceding)
            return $"Moving away — closest it got was {Units.Distance(approach.DistanceKm)}";
        if (approach.IsStationary)
            return $"{Units.Distance(approach.CurrentKm)} from home, motion not tracked yet";
        if (approach.DistanceKm > _threats.RadiusKm)
            return $"Closest approach to home: {Units.Distance(approach.DistanceKm)}";

        string when = approach.EtaMinutes < 1 ? "now" : $"in ~{approach.EtaMinutes:F0} min";
        if (approach.DistanceKm <= _threats.DirectHitRadiusKm)
            return $"⚠ Heading for you — within {Units.Distance(approach.DistanceKm)} {when}";

        string side = ThreatMonitor.CompassPoint(
            GeoMath.BearingRad(_threats.HomeLatDeg!.Value, _threats.HomeLonDeg!.Value,
                approach.LatDeg, approach.LonDeg) * 180.0 / Math.PI);
        return $"Passes {Units.Distance(approach.DistanceKm)} to your {side} {when} — not on course for you";
    }

    private void ShowStormPopup(TrackedStorm storm, int x, int y)
    {
        var lines = new List<string>
        {
            storm.SpeedKmh is { } kmh && storm.BearingDeg is { } deg
                ? $"Moving {ThreatMonitor.CompassPoint(deg)} ({deg:F0}°) at {Units.Speed(kmh)}"
                // Saying "0 mph" here would claim the cell was measured to be standing still.
                : "Motion not tracked yet — the algorithm needs a second scan of this cell.",
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
            lines.Add(ApproachLine(approach));

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
        if (_settings.Locations.Count == 0)
        {
            _homeGeometry = null;
            return;
        }

        var geometry = new OverlayGeometry();
        _homeBuiltAtMetresPerPixel = _mapView.Camera.Snapshot().MetersPerPixel;

        foreach (var place in _settings.Locations)
        {
            var centre = GeoMath.ToMercator(place.LatDeg, place.LonDeg);
            // The primary reads brighter: it is the one the app opens on and watches storms
            // from, so it is worth being able to pick out at a glance.
            uint color = place.IsPrimary
                ? OverlayGeometry.Pack(80, 200, 255, 235)
                : OverlayGeometry.Pack(80, 200, 255, 150);

            // Radius ring drawn in true kilometres; Mercator inflates by 1/cos(lat).
            double mercatorRadius = _settings.RadiusFor(place) * 1000.0
                / Math.Cos(place.LatDeg * Math.PI / 180.0);
            StormOverlayController.AddCircle(geometry, (centre.X, centre.Y), mercatorRadius, color, 3.5f);

            // The marker itself is a symbol, so it is sized in pixels, not metres.
            double s = 6 * _homeBuiltAtMetresPerPixel;
            geometry.FillTriangles.Add((centre.X - s, centre.Y - s, color));
            geometry.FillTriangles.Add((centre.X, centre.Y + s, color));
            geometry.FillTriangles.Add((centre.X + s, centre.Y - s, color));
        }
        _homeGeometry = geometry;
    }

    /// <summary>
    /// Hand the monitor every saved place, primary first. Order matters only in that the
    /// monitor treats the first as the one the map marker and storm popup measure against.
    /// </summary>
    private void ConfigureThreats()
    {
        var places = _settings.Locations
            .OrderByDescending(l => l.IsPrimary)
            .Select(l => new WatchedPlace(l.Name, l.LatDeg, l.LonDeg, _settings.RadiusFor(l)))
            .ToList();
        _threats.Configure(places, _settings.DirectHitRadiusKm);
    }

    /// <summary>
    /// Rebuild the home marker when the zoom moves materially — the same contract the storm,
    /// lightning and MRMS overlays have, and for the same reason: the triangle is a symbol
    /// sized in screen pixels, so its metre extent is only correct for the zoom it was built
    /// at. Left out of the tick, it kept whatever size it was given during startup, which is
    /// a national-zoom size — an 80 km triangle once the camera is over a single site. The
    /// radius ring is a true ground extent and is right at any zoom; only the marker moves.
    /// </summary>
    private void NotifyHomeViewChanged()
    {
        if (_homeGeometry is null) return;

        double now = _mapView.Camera.Snapshot().MetersPerPixel;
        if (Math.Abs(now - _homeBuiltAtMetresPerPixel) / Math.Max(now, 1e-6) <= 0.05) return;

        RebuildHomeGeometry();
        ComposeOverlay();
    }

    /// <summary>
    /// Redraws the approaching list. The collection is rebuilt in place rather than reassigned
    /// so the ItemsControl keeps its scroll position across a refresh — a list that jumps back
    /// to the top every two minutes cannot be read.
    /// </summary>
    private void ShowThreatList(IReadOnlyList<Threat> threats)
    {
        _vm.Threats.Clear();
        foreach (var threat in threats) _vm.Threats.Add(threat);

        ThreatPanel.Visibility = threats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncRightColumn();
        if (threats.Count == 0) return;

        int tornadic = threats.Count(t => t.Rank == ThreatRank.Tornadic);
        int glancing = threats.Count(t => t.Rank == ThreatRank.Glancing);
        int coming = threats.Count - glancing;

        // The count separates the two claims rather than adding them up: "4" over a list
        // where three of the four are passing wide overstates it every time.
        ThreatCount.Text = tornadic > 0 ? $"{coming} · {tornadic} tornadic"
            : glancing > 0 && coming > 0 ? $"{coming} · {glancing} passing wide"
            : glancing > 0 ? $"{glancing} passing wide"
            : $"{coming}";

        // The heading carries the worst of it, because the heading is what gets read at a
        // glance from across the room — and when nothing is actually coming, it says so.
        ThreatHeading.Text = tornadic > 0 ? "TORNADIC — APPROACHING"
            : coming > 0 ? "APPROACHING"
            : "IN THE AREA";
        ThreatHeading.Foreground = tornadic > 0
            ? System.Windows.Media.Brushes.OrangeRed
            : coming > 0
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x9B, 0x7A))
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x9A, 0xA3, 0xB2));
    }

    /// <summary>Put the camera on the threat that was clicked, at a single-storm zoom.</summary>
    private void ThreatRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Threat threat }) return;
        _mapView.Camera.MoveTo(threat.LatDeg, threat.LonDeg, 150);
        Report(threat.Detail);
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

    /// <summary>
    /// Native ABI is the layer; the pre-rendered tiles are the fallback. Both are armed
    /// together so a bucket outage degrades to the tiles rather than to nothing, and the
    /// renderer draws the tiles only while no native raster is loaded — they are the same
    /// field, so showing both at once is never right.
    /// </summary>
    private void SatelliteFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_mapView is null || _satellite is null) return;
        bool on = FilterSatellite.IsChecked == true;
        _mapView.SatelliteEnabled = on;
        if (on) _satellite.Enable(); else _satellite.Disable();
        SatelliteNoteText.Text = on ? "Fetching…" : "";
    }

    private void SatelliteOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is not null)
            _mapView.SatelliteOpacity = (float)(e.NewValue / 100.0);
        _satellite?.SetOpacity((float)(e.NewValue / 100.0));
    }

    private void Boundaries_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the controller exists.
        if (_boundaries is null || _settings is null) return;

        _settings.ShowStateLines = FilterStates.IsChecked == true;
        _settings.ShowCountyLines = FilterCounties.IsChecked == true;
        _boundaries.SetVisible(BoundarySet.States, _settings.ShowStateLines);
        _boundaries.SetVisible(BoundarySet.Counties, _settings.ShowCountyLines);
        _settings.Save();
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
        var moving = storms.Where(s => s is { SpeedKmh: > 3, BearingDeg: not null }).ToList();
        if (moving.Count == 0) return;

        double u = moving.Average(s => s.SpeedKmh!.Value * Math.Sin(s.BearingDeg!.Value * Math.PI / 180.0));
        double v = moving.Average(s => s.SpeedKmh!.Value * Math.Cos(s.BearingDeg!.Value * Math.PI / 180.0));
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

    /// <summary>
    /// The hail swath is a derived field read against the echo, so it draws over the sweep
    /// rather than under it and does not compete for the Field slot the mosaics share.
    /// </summary>
    private void HailField_Changed(object sender, RoutedEventArgs e)
    {
        if (_hailField is null || FilterHailField is null) return;
        if (FilterHailField.IsChecked == true) _hailField.Enable();
        else _hailField.Disable();
    }

    private void HailFieldOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_hailField is not null) _hailField.Opacity = (float)(e.NewValue / 100.0);
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

    // ---- 3D volume ----

    private async void Volume_Changed(object sender, RoutedEventArgs e)
    {
        if (VolumePanel is null) return;
        bool on = VolumeToggle.IsChecked == true;
        VolumePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // The product bar still applies in 3D — a volume of velocity is as valid as one of
        // reflectivity — but the tilt picker does not: 3D is every cut at once.
        TiltPanel.IsEnabled = !on;

        if (on)
        {
            SyncVolumeSettings();
            await _volume.EnterAsync();
            ResetVolumeCamera();
        }
        else
        {
            _volume.Leave();
        }
    }

    /// <summary>Push the panel's values through before a build, so the first frame is right.</summary>
    private void SyncVolumeSettings()
    {
        _mapView.VolumeThreshold = (float)VolumeThresholdSlider.Value;
        _mapView.VolumeDensity = (float)VolumeDensitySlider.Value;
        _mapView.VolumeExaggeration = (float)VolumeStretchSlider.Value;
        SyncVolumeLabels();
    }

    private void SyncVolumeLabels()
    {
        if (VolumeThresholdLabel is null) return;
        // The threshold is in the product's own units, so the label has to say which.
        string unit = _radar.CurrentMoment switch
        {
            Moment.Reflectivity => "dBZ",
            Moment.Velocity => "m/s",
            Moment.SpectrumWidth => "m/s",
            _ => "",
        };
        VolumeThresholdLabel.Text = $"Threshold — {VolumeThresholdSlider.Value:F0} {unit}".TrimEnd();
        VolumeDensityLabel.Text = $"Density — {VolumeDensitySlider.Value:F2}";
        VolumeStretchLabel.Text = $"Vertical stretch — {VolumeStretchSlider.Value:F0}×";
    }

    private void VolumeThreshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is null) return;
        _mapView.VolumeThreshold = (float)e.NewValue;
        SyncVolumeLabels();
    }

    private void VolumeDensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is null) return;
        _mapView.VolumeDensity = (float)e.NewValue;
        SyncVolumeLabels();
    }

    private void VolumeStretch_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mapView is null) return;
        _mapView.VolumeExaggeration = (float)e.NewValue;
        SyncVolumeLabels();
        // The camera framed the old box height; keep it framing the new one.
        if (_volume?.IsActive == true) ResetVolumeCamera();
    }

    private async void VolumeRange_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_volume is null) return;
        if (VolumeRangeCombo.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !double.TryParse(tag, out double halfWidth))
            return;

        _volume.HalfWidthM = halfWidth;
        if (!_volume.IsActive) return;
        await _volume.RebuildAsync();
        ResetVolumeCamera();
    }

    private void VolumeReset_Click(object sender, RoutedEventArgs e) => ResetVolumeCamera();

    /// <summary>
    /// Frame the whole box from the south, looking north — the orientation a radar operator
    /// already has in their head from every plan view they have ever seen.
    /// </summary>
    private void ResetVolumeCamera()
    {
        float top = (float)(VolumeController.TopHeightM * _mapView.VolumeExaggeration);
        _mapView.VolumeCamera.Reset(
            new System.Numerics.Vector3(0, 0, top * 0.35f),
            _volume.HalfWidthM * 2.6);
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
            ClearHodograph();
            return;
        }

        WindProfileNote.Text = "Fitting…";
        var levels = await Task.Run(() => VadProfile.Compute(sweeps));

        if (levels.Count == 0)
        {
            WindProfileList.ItemsSource = null;
            WindProfileNote.Text =
                "No level had enough echo around the radar to fit a wind. Clear air often will not.";
            ClearHodograph();
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

        DrawHodograph(levels, sweeps[0].RadarLatDeg, sweeps[0].RadarLonDeg);
    }

    /// <summary>
    /// Plot the profile and report the helicity in it.
    /// </summary>
    /// <remarks>
    /// Helicity is measured against the <em>observed</em> motion of a tracked cell when there
    /// is one. That is the advantage of computing this inside a radar application rather than
    /// from a sounding: a sounding has to estimate where a storm would go, and this knows
    /// where one actually went. With nothing tracked it falls back to the mean wind through
    /// the layer, and says which it used, because the number means different things.
    /// </remarks>
    private void DrawHodograph(IReadOnlyList<VadLevel> levels, double radarLatDeg, double radarLonDeg)
    {
        var points = Hodograph.FromProfile(levels);
        if (points.Count < 2)
        {
            ClearHodograph();
            return;
        }

        var tracked = _storms.Storms
            .Where(s => s.SpeedKmh is > 3 && s.BearingDeg is not null)
            .OrderBy(s => GeoMath.DistanceM(s.LatDeg, s.LonDeg, radarLatDeg, radarLonDeg))
            .FirstOrDefault();

        (double UMs, double VMs) motion;
        string basis;
        if (tracked is not null)
        {
            // A storm bearing is the direction it is heading, not where it comes from, so
            // this carries no negative sign — unlike the wind conversion.
            double radians = tracked.BearingDeg!.Value * Math.PI / 180.0;
            double speedMs = tracked.SpeedKmh!.Value / 3.6;
            motion = (speedMs * Math.Sin(radians), speedMs * Math.Cos(radians));
            basis = $"vs cell {tracked.Id}";
        }
        else
        {
            motion = Hodograph.MeanWind(points, 6000);
            basis = "vs 0–6 km mean wind";
        }

        bool knots = Units.System != UnitSystem.Metric;
        HodographImage.Source = HodographPlot.Build(points, motion, 176, knots);
        HodographImage.Visibility = HodographImage.Source is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Only quote a layer the profile actually spans. Hodograph.Layer returns what it has
        // when asked for more, so labelling a 2.7 km profile's shear "0–6 km" would be a
        // plausible-looking number that is simply not the quantity named.
        double depthM = Hodograph.DepthM(points);
        var lines = new List<string>();

        var helicity = new List<string>();
        foreach (int km in new[] { 1, 3 })
            if (depthM >= km * 1000)
            {
                var (_, _, srh) = Hodograph.StormRelativeHelicity(
                    points, motion.UMs, motion.VMs, km * 1000);
                helicity.Add($"0–{km} {srh:F0}");
            }
        if (helicity.Count > 0) lines.Add($"SRH {string.Join("  ", helicity)} m²/s²");

        int shearKm = depthM >= 6000 ? 6 : (int)(depthM / 1000);
        if (shearKm >= 1)
        {
            var shear = Hodograph.BulkShear(points, shearKm * 1000);
            double magnitude = knots ? shear.MagnitudeMs * 1.943844 : shear.MagnitudeMs;
            lines.Add($"0–{shearKm} shear {magnitude:F0} {(knots ? "kt" : "m/s")}");
        }

        lines.Add(depthM < 3000
            ? $"{basis} · profile only {depthM / 1000:F1} km deep"
            : basis);

        HodographNote.Text = string.Join(Environment.NewLine, lines);
    }

    private void ClearHodograph()
    {
        HodographImage.Source = null;
        HodographImage.Visibility = Visibility.Collapsed;
        HodographNote.Text = "";
    }

    /// <summary>
    /// Restore each panel section to however it was left, and keep it that way.
    /// </summary>
    /// <remarks>
    /// Hooked generically by walking for <see cref="Expander"/>s rather than by wiring nine
    /// handlers in XAML, so a section added later is remembered without anyone remembering to
    /// make it so. Keyed by header text: a name would be tidier but every one of these already
    /// has a header, and an unkeyed section would silently stop persisting.
    /// </remarks>
    private void RestorePanelSections()
    {
        foreach (var section in FindExpanders(LayersPanel))
        {
            if (section.Header is not string header || header.Length == 0) continue;

            if (_settings.PanelSections.TryGetValue(header, out bool open))
                section.IsExpanded = open;

            section.Expanded += PanelSection_Changed;
            section.Collapsed += PanelSection_Changed;
        }
    }

    private void PanelSection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Header: string header } section) return;
        _settings.PanelSections[header] = section.IsExpanded;
        _settings.Save();
    }

    private static IEnumerable<Expander> FindExpanders(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Expander expander) yield return expander;
            foreach (var nested in FindExpanders(child)) yield return nested;
        }
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

    private async void ImportAddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a GeoJSON or shapefile",
            Filter = ShapeImportController.FileFilter,
        };
        if (dialog.ShowDialog(this) != true) return;
        await _imports.AddAsync(dialog.FileName);

        if (_imports.Files.Any(f => f.Path == dialog.FileName)
            && !_settings.ImportedShapes.Contains(dialog.FileName))
        {
            _settings.ImportedShapes.Add(dialog.FileName);
            _settings.Save();
        }
    }

    private void ImportToggled(object sender, RoutedEventArgs e)
    {
        if (_imports is null || sender is not FrameworkElement { Tag: ImportedShapes entry }) return;
        _imports.SetEnabled(entry, ((System.Windows.Controls.CheckBox)sender).IsChecked == true);
    }

    private void ImportRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_imports is null || sender is not FrameworkElement { Tag: ImportedShapes entry }) return;
        _imports.Remove(entry);
        if (_settings.ImportedShapes.Remove(entry.Path)) _settings.Save();
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
        SyncRightColumn();
    }

    /// <summary>
    /// The column itself goes away only when both halves are hidden — otherwise hiding the
    /// layers leaves 248 px of empty chrome beside the map.
    /// </summary>
    private void SyncRightColumn()
    {
        if (RightColumn is null || LayersPanel is null || ThreatPanel is null) return;
        RightColumn.Visibility =
            LayersPanel.Visibility == Visibility.Visible || ThreatPanel.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
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
        // Shown rather than merely enabled: a permanently greyed button is a slot the rail
        // cannot spare for something that does nothing until a second pane exists.
        LinkToggle.Visibility = _paneCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        LinkToggle.IsEnabled = _paneCount > 1;
    }

    /// <summary>
    /// Import a GR2Analyst colour table for whichever product is showing. Reached from
    /// Settings rather than the rail: it is set once per product and then never touched,
    /// which is the line this shell draws between the two places.
    /// </summary>
    private void ImportPalette()
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
        ConfigureThreats();
        // Settings can now set home outright (Use my location), not only arm the map picker,
        // so the storm watch has to be re-pointed here as well as on the map-click path.
        EnsureStormWatchForHome();
        RebuildHomeGeometry();
        ComposeOverlay();
        _radar.Refresh();

        if (dialog.WantsHomePicker)
        {
            _vm.Tool = MapTool.SetHome;
            SyncToolButtons();
            return;
        }
        if (dialog.WantsPaletteImport)
        {
            ImportPalette();
            return;
        }
        Report("Settings saved. Basemap changes take effect next launch.");
    }

    /// <summary>
    /// The guide, not the shortcut table. "?" is what someone presses when they do not know
    /// how the thing works, and the first question is what it will do on their behalf — the
    /// keys and the symbol key are a click away from there.
    /// </summary>
    private void HelpButton_Click(object sender, RoutedEventArgs e) =>
        InfoWindow.ShowGuide(this);

    private string Version =>
        GetType().Assembly.GetName().Version?.ToString(3) ?? "dev";

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
