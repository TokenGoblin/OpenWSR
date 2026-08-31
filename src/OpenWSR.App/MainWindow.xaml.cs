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

    /// <summary>
    /// Set only by Exit, and the difference between hiding and quitting. Without it the
    /// close-to-tray check in <see cref="OnClosing"/> would cancel the very close that Exit
    /// asks for, and the app could not be shut down at all.
    /// </summary>
    private bool _exiting;

    /// <summary>
    /// Whether the window is hidden and the app is running as a tray watcher. Read by the
    /// things that only make sense with a window on screen — the Level II stream, the
    /// in-window toast — so they can stand down rather than work for nobody.
    /// </summary>
    private bool _inTray;

    /// <summary>
    /// The state to come back to. Tracked as the window changes rather than read on the way
    /// out, because minimise-to-tray hides from the minimised state — by which point
    /// <see cref="Window.WindowState"/> reads Minimized and no longer remembers that the
    /// window was maximised, so restoring would quietly un-maximise it. Starts at Maximized
    /// because that is what the XAML opens with.
    /// </summary>
    private WindowState _restoreState = WindowState.Maximized;

    /// <summary>
    /// When to collect for the second time after hiding, or null when none is due.
    ///
    /// Hiding collects immediately, but a fetch that was already in flight lands after that
    /// and re-inflates the heap — measured at 2.0 GB three minutes after a hide, from a 61 MB
    /// satellite granule whose decode was underway when the window went away. One more pass,
    /// once the stragglers have finished, catches exactly that. It is deliberately not a
    /// recurring sweep: with the drawing clocks stopped nothing else allocates, so there
    /// would be nothing for a second one to find.
    /// </summary>
    private DateTime? _traySettleDueUtc;
    private bool _settingsSaveDue;
    private DateTime _settingsChangedAtUtc;

    /// <summary>
    /// Note that settings changed, and write them once the flurry stops.
    /// </summary>
    /// <remarks>
    /// A snapped slider raises ValueChanged on every tick, so dragging one end of the dBZ
    /// window across its range was twenty-odd whole-document writes on the UI thread. The
    /// value is applied live either way; only writing it down waits.
    /// </remarks>
    private void MarkSettingsDirty()
    {
        _settingsSaveDue = true;
        _settingsChangedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Everything that runs on a clock to *draw* something, as opposed to the two polls that
    /// watch for threats. These stop while OpenWSR is in the tray; see <see cref="ITimedLayer"/>
    /// for why that is not the same as switching them off.
    /// </summary>
    private IEnumerable<ITimedLayer> TimedLayers
    {
        get
        {
            if (_satellite is not null) yield return _satellite;
            yield return _mrms;
            yield return _hailField;
            yield return _lightning;
            yield return _placefiles;
            yield return _outlooks;
            yield return _future;
            yield return _playback;
        }
    }

    /// <summary>Write settings down once the flurry has settled, rather than per tick.</summary>
    private void FlushSettingsIfDue()
    {
        if (!_settingsSaveDue) return;
        if (DateTime.UtcNow - _settingsChangedAtUtc <= TimeSpan.FromSeconds(1)) return;
        _settingsSaveDue = false;
        _settings.Save();
    }
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
        if (App.StartInTray || settings.StartInTray)
        {
            // Minimised and unactivated rather than hidden, because the D3D surface is an
            // HwndHost and only builds its child window once the window it sits in has been
            // laid out — hide before that and there is no renderer to come back to. The
            // Loaded handler does the actual hiding, a frame later; this is what stops the
            // window appearing on screen in the meantime.
            WindowState = WindowState.Minimized;
            ShowActivated = false;
            ShowInTaskbar = false;
        }
        Units.System = settings.Units;
        _geocoder = new Geocoder(settings.UserAgent);
        var provider = settings.TileProvider switch
        {
            "maptiler" when !string.IsNullOrEmpty(settings.MapTilerKey) =>
                TileProvider.MapTiler(settings.MapTilerKey, settings.UserAgent),
            "osm" => TileProvider.Osm(settings.UserAgent),
            "usgs-topo" => TileProvider.UsgsTopo(settings.UserAgent),
            // Anything else — a value from a newer build, a corrupt file, MapTiler selected
            // with no key — gets the default rather than the light map, so a settings file
            // nobody can read still opens on the basemap AppSettings promises.
            _ => TileProvider.OsmDark(settings.UserAgent),
        };
        AttributionText.Text = provider.Name switch
        {
            "maptiler" => "© MapTiler © OpenStreetMap contributors",
            "usgs-topo" => "USGS The National Map",
            _ => "© OpenStreetMap contributors",
        };

        _mapView = new MapView(provider);
        // The sliders' XAML values are the shipped defaults, but the handlers that apply them
        // ran during InitializeComponent, when _mapView was still null, and dropped them on the
        // floor. Until now that was invisible because the two ends were kept equal by hand —
        // Value="85" against a _radarOpacity of 0.85f — which is a coincidence one edit away
        // from a panel that disagrees with the picture. Push them once, so the XAML is the only
        // place a default is written. Settings restore, further down, overrides these with
        // whatever the user has actually changed.
        ApplyRadarAppearance();
        _mapView.Camera.MoveTo(39.0, -98.0, 6000); // continental US
        _mapView.SetMarkers(RadarSites.All.Select(s => (s.LatDeg, s.LonDeg, s.Icao)));
        _mapView.MarkersEnabled = settings.ShowSiteMarkers;
        FilterSites.IsChecked = settings.ShowSiteMarkers;
        // Setting Value raises ValueChanged synchronously. Left unsuppressed, restoring the
        // minimum ran the handler while the maximum still held its XAML default, which wrote
        // that default back over the saved one — and the handler reaches _radar, which is not
        // constructed until further down this constructor.
        _suppressSliderEvents = true;
        DbzMinSlider.Value = Math.Min(settings.DbzFilterMin, settings.DbzFilterMax);
        DbzMaxSlider.Value = Math.Max(settings.DbzFilterMin, settings.DbzFilterMax);
        _suppressSliderEvents = false;
        FilterStates.IsChecked = settings.ShowStateLines;
        FilterCounties.IsChecked = settings.ShowCountyLines;
        MapHost.Child = new D3DHostControl(_mapView);

        _radar = new RadarDisplayController(_mapView);
        ApplyDbzFilter(); // the sliders were restored above, before this existed

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
        _boundaries.LoadFailed += set => Dispatcher.BeginInvoke(() =>
        {
            // Put the tick back where the layer actually is, so retrying is one click.
            if (set == BoundarySet.States) FilterStates.IsChecked = false;
            else FilterCounties.IsChecked = false;
        });

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
        _tray.Activated += () => Dispatcher.BeginInvoke(RestoreFromTray);
        _tray.SettingsRequested += () => Dispatcher.BeginInvoke(() =>
        {
            // Settings is modal on this window, so the window has to exist on screen first —
            // a modal dialog owned by a hidden window is one nobody can find.
            RestoreFromTray();
            SettingsButton_Click(this, new RoutedEventArgs());
        });
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(ExitApplication);
        // Windows ends the session by calling Application.Shutdown, which still raises
        // Closing — so without this, logging off would run the close-to-tray path: it would
        // spend the one-time "OpenWSR is still watching" balloon on someone who is signing
        // out, and the next time they genuinely closed the window it would vanish with no
        // explanation at all.
        Application.Current.SessionEnding += (_, _) => _exiting = true;
        // A second launch is how someone asks for a window they cannot see. Bring this one
        // back rather than letting a duplicate start; see SingleInstance.
        App.ActivationRequested += () => Dispatcher.BeginInvoke(RestoreFromTray);

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
            // In the tray there is no panel to paint and no camera to follow, so the whole
            // tick is work for nobody — bar the two parts that are about data rather than
            // pixels: writing down a pending settings change, and dropping warnings that have
            // run out from the set the tray line reports.
            if (_inTray)
            {
                FlushSettingsIfDue();
                _threats.ExpireStale(DateTimeOffset.UtcNow);
                if (_traySettleDueUtc is { } due && DateTime.UtcNow > due)
                {
                    _traySettleDueUtc = null;
                    CollectIdleMemory("settled");
                }
                return;
            }

            FpsText.Text = $"{_mapView.FramesPerSecond:F0} fps · sweep upload {_mapView.LastSweepUploadMs:F1} ms";
            UpdateCentreReadout();

            FlushSettingsIfDue();

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

            // Display scaling decides stroke weights and the swap chain size, and "it looks
            // thin on his machine" is very hard to act on without knowing it.
            Serilog.Log.Information(
                "Display scaling {Percent}%; map surface {Width}x{Height} px",
                (int)Math.Round(_mapView.DipScale * 100),
                (int)_mapView.Camera.Snapshot().ViewportWidth,
                (int)_mapView.Camera.Snapshot().ViewportHeight);

            // Straight to the tray when Windows started us at login, or when the user asked
            // for it. Before the restore, so that the layers it switches on find polling
            // already suspended and never fetch at all — a tray start with the satellite layer
            // ticked would otherwise pull a 61 MB granule to paint a hidden window.
            if (App.StartInTray || settings.StartInTray) HideToTray();

            RestorePanelSections();
            RestoreLayerState();

            // And the storm watch is re-armed after it, because the restore replays the saved
            // toggles and can switch the storm layer straight back off — which left the app
            // sitting in the tray reporting that it was watching while no storm poll ran at
            // all. Cheap and idempotent: it re-points an already-armed watch.
            if (_inTray) EnsureStormWatchForHome();

            foreach (var source in settings.Placefiles.ToList())
                await _placefiles.AddAsync(source);
            foreach (var path in settings.ImportedShapes.ToList())
            {
                if (System.IO.File.Exists(path)) await _imports.AddAsync(path);
                else if (settings.ImportedShapes.Remove(path)) settings.Save();
            }
            await LoadStartupAsync();
            // Not while hidden: it would open on the bare desktop, and marking it shown
            // would spend the first-run guide on somebody who never saw it.
            if (!settings.WelcomeShown && !_inTray)
            {
                settings.WelcomeShown = true;
                settings.Save();
                InfoWindow.ShowGuide(this);
            }
        };
        Closed += (_, _) =>
        {
            // A change made in the last second before closing has not been written yet.
            if (_settingsSaveDue) { _settingsSaveDue = false; _settings.Save(); }

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

    // ---- running in the tray ----
    //
    // OpenWSR is a watchman as much as a viewer, and a watchman that only works while its
    // window is open is not much of one. Hidden, it keeps the two polls the alarm is actually
    // built on — Level III storm tracks every two minutes and api.weather.gov warnings every
    // minute — and stands everything else down.

    /// <summary>
    /// Closing leaves OpenWSR watching in the tray rather than exiting.
    ///
    /// This redefines the close button, which is not a thing to do lightly, and it is done for
    /// a specific reason: the alerting is worth nothing while the process is not running, and
    /// the close button is how people tidy a desktop rather than how they decide to stop being
    /// warned about tornadoes. Three things keep it from being a trap — a one-time
    /// notification the first time it happens saying where the app went, Exit in the tray
    /// menu, and <see cref="AppSettings.CloseToTray"/> for anyone who disagrees.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Minimising can hide to the tray too, but only if asked. Minimise already has a meaning
    /// everybody knows, and quietly taking the taskbar button away with it is a worse surprise
    /// than the close button being redefined: there a notification explains itself, whereas a
    /// vanished taskbar button reads as a crash.
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
        else if (!_exiting && _settings.MinimiseToTray) HideToTray();
    }

    /// <summary>
    /// Become a tray watcher: hide the window, stop everything whose only purpose is putting
    /// pixels on it, and make sure the watch is actually armed.
    /// </summary>
    private void HideToTray()
    {
        if (_inTray) return;
        _inTray = true;

        Hide();
        ShowInTaskbar = false;
        _mapView.Paused = true;

        // The Level II stream is the most expensive thing this app does — a volume is about
        // 210 MB once decoded to floats, and the working set sawtooths to 1.45 GB with it
        // running — and not one byte of it feeds the alarm. Threats come from the Level III
        // storm-track poll and the warnings poll, a few kilobytes a minute between them. So
        // the stream stands down while the watch does not. The mode is deliberately left as
        // it was, so coming back is a matter of starting it again rather than reconstructing
        // what the user was looking at.
        if (_vm.IsLive) _ = _liveFeed.StopAsync();

        // Everything else on a clock draws rather than watches, and drawing is what has
        // stopped. Satellite is the one that makes this necessary rather than tidy: a
        // multiband ABI granule is about 57 MB every four minutes, so a hidden window with
        // that layer ticked would pull the better part of a gigabyte an hour to paint a
        // surface nobody is looking at.
        foreach (var layer in TimedLayers) layer.SuspendPolling();

        // Nothing is watched at all unless the storm poll is pointed at a radar. With the
        // window up, whether the storm layer is on is the user's business; here it is the
        // entire reason the process is still running.
        EnsureStormWatchForHome();
        _tray.SetStatus(_threats.Places, _threats.Current);

        Serilog.Log.Information(
            "Hidden to the tray, watching {Places} place(s)", _threats.Places.Count);
        CollectIdleMemory("hidden");
        _traySettleDueUtc = DateTime.UtcNow + TimeSpan.FromMinutes(2);

        if (_settings.TrayHintShown) return;
        // Said once, the first time the window disappears. Someone who does not know where the
        // app went concludes it crashed, and the people who conclude that are exactly the ones
        // who will not be there to read the storm alert an hour later.
        _settings.TrayHintShown = true;
        _settings.Save();
        _tray.Notify(
            "OpenWSR is still watching",
            _threats.IsArmed
                ? "It is down in the notification area, and will alert you if a storm heads "
                + "your way. Right-click the icon to open it again, or to exit."
                : "It is down in the notification area. Add a place in Settings to arm "
                + "proximity alerts. Right-click the icon to open it again, or to exit.",
            urgent: false);
    }

    /// <summary>
    /// Ask for the memory back rather than waiting for a collection that may never come.
    ///
    /// Normally the wrong instinct — the runtime tunes this better than a guess does — but
    /// every assumption behind that advice has just stopped holding. A live volume's worth of
    /// decoded floats has been dropped, the clocks that would allocate again are stopped, and
    /// the process is about to sit idle for hours rather than seconds; left alone it simply
    /// keeps the peak, because there is no pressure to make it do otherwise. The pause it
    /// costs lands on a window that is already hidden. Compaction is the part that matters:
    /// a decoded sweep is large-object-heap sized, and without it the address space stays
    /// booked even once the objects are gone.
    /// </summary>
    private void CollectIdleMemory(string reason)
    {
        long before = GC.GetTotalMemory(false);
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Serilog.Log.Information(
            "Idle collection ({Reason}): heap {Before} MB -> {After} MB",
            reason, before >> 20, GC.GetTotalMemory(false) >> 20);
    }

    /// <summary>Come back: show the window, resume drawing, and pick the live feed up again.</summary>
    private void RestoreFromTray()
    {
        bool wasHidden = _inTray;
        _inTray = false;
        _traySettleDueUtc = null;   // about to allocate again; the pass would be wasted

        ShowInTaskbar = true;
        // Cleared in the constructor for a tray start, and never restored otherwise — every
        // later Show() would come back without taking the foreground.
        ShowActivated = true;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        _mapView.Paused = false;
        Activate();

        foreach (var layer in TimedLayers) layer.ResumePolling();

        // The mode was left alone on the way in, so "still live" only has to be made true
        // again. Not conditional on how long it was away: a volume from before the pause is
        // stale by definition, and reconnecting is what stops the age indicator lying.
        //
        // Without a site to frame, though: StartLiveAsync normally moves the camera to the
        // radar, which is right when you pick a site and wrong here — coming back from the
        // tray would throw away wherever you had panned and zoomed to.
        if (wasHidden && _vm.IsLive) StartLiveAsync(frameSite: false);
    }

    /// <summary>
    /// Quit for real. The only way out once the window is hidden, which is why it is in the
    /// tray menu — an app that can only be exited from a window it has put away is a trap.
    /// </summary>
    private void ExitApplication()
    {
        _exiting = true;
        Close();
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
        ErrorBar.Background = ErrorBackground;
        ErrorBar.BorderBrush = ErrorBorder;
        ErrorText.Foreground = ErrorForeground;
        ErrorBar.Visibility = Visibility.Visible;
        Serilog.Log.Warning("UI error surfaced: {Text}", text);
    }

    /// <summary>
    /// A persistent, dismissible notice that is not a failure.
    /// </summary>
    /// <remarks>
    /// <see cref="Report"/> is the running commentary and is overwritten within seconds by
    /// the live feed, which is right for a commentary and wrong for telling someone what the
    /// app has just done on their behalf — saving a location it found, say. That needs the
    /// same persistence as an error and none of the alarm, so it borrows the bar and changes
    /// its colours.
    /// </remarks>
    public void ReportNotice(string text)
    {
        // Never over an error. They share one bar, and a notice saying something went to
        // plan must not bury a failure the user still has to act on.
        if (ErrorBar.Visibility == Visibility.Visible && ErrorBar.Background == ErrorBackground)
        {
            Report(text);
            return;
        }

        ErrorText.Text = text;
        ErrorBar.Background = NoticeBackground;
        ErrorBar.BorderBrush = NoticeBorder;
        ErrorText.Foreground = NoticeForeground;
        ErrorBar.Visibility = Visibility.Visible;
        Serilog.Log.Information("UI notice surfaced: {Text}", text);
    }

    private static readonly System.Windows.Media.Brush ErrorBackground =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x3A, 0x22, 0x26));
    private static readonly System.Windows.Media.Brush ErrorBorder =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x8E, 0x40, 0x48));
    private static readonly System.Windows.Media.Brush ErrorForeground =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0xC8));

    private static readonly System.Windows.Media.Brush NoticeBackground =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x1E, 0x2E, 0x3A));
    private static readonly System.Windows.Media.Brush NoticeBorder =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x3C, 0x6E, 0x8E));
    private static readonly System.Windows.Media.Brush NoticeForeground =
        new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xC8, 0xE4, 0xFF));

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

        // The dBZ window is meaningless against velocity or CC, so it follows the product.
        ApplyDbzFilter();

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

    /// <param name="frameSite">
    /// Whether to move the camera onto the site. True when the user has chosen a radar or a
    /// mode, false when the feed is merely being picked back up — resuming from the tray must
    /// not discard where they were looking.
    /// </param>
    private async void StartLiveAsync(bool frameSite = true)
    {
        if (SiteCombo.SelectedItem is not RadarSite site)
        {
            ReportError("Pick a radar site before starting the live feed.");
            ApplyMode(DataMode.Archive);
            return;
        }
        if (frameSite) FrameSite(site);

        // Hidden in the tray there is nobody to show a volume to, and this stream is the whole
        // difference between a background watcher and a gigabyte of resident radar decode. The
        // alarm does not use it — see HideToTray.
        if (_inTray)
        {
            LiveStateText.Text = "Live radar paused while OpenWSR is in the tray.";
            return;
        }

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

        // Not awaited. Windows can sit on a cold radio for twelve seconds, and the national
        // view is a perfectly good thing to be looking at meanwhile — holding the first paint
        // hostage to an OS call would make the app feel broken to anyone whose location is off.
        _ = TryLocateOnFirstRunAsync();
    }

    /// <summary>
    /// On a first run with no saved place, ask Windows where we are and keep the answer.
    /// </summary>
    /// <remarks>
    /// This is the whole setup step for a new user: with a place saved the app opens on its
    /// radar, watches storms from it, and can say what is heading for it. Without one it is a
    /// browser for other people's weather.
    ///
    /// <para>It runs at most once — see <see cref="AppSettings.LocationAsked"/> — and it never
    /// overwrites an existing place, so someone who has set a home deliberately is not
    /// second-guessed by the OS's idea of where they are.</para>
    /// </remarks>
    private async Task TryLocateOnFirstRunAsync()
    {
        if (_settings.LocationAsked || _settings.Locations.Count > 0) return;

        // Recorded before the attempt, not after: if the call throws, hangs or the app is
        // closed mid-way, the intent was still "we have asked once".
        _settings.LocationAsked = true;
        _settings.Save();

        Report("Checking Windows for your location…");
        LocationFix fix;
        try
        {
            fix = await GeoLocationService.GetCurrentAsync();
        }
        catch (GeoLocationService.LocationUnavailableException ex)
        {
            // The message names the two privacy switches, which is the only useful thing to
            // say here. Shown once, because LocationAsked is already set.
            ReportError($"{ex.Message} You can add a place by hand in Settings at any time.");
            return;
        }
        catch (Exception ex)
        {
            // Nothing awaits this, so anything escaping would be an unobserved task: the app
            // would do nothing and say nothing, having already recorded that it asked.
            Serilog.Log.Warning(ex, "First-run location lookup failed unexpectedly");
            ReportError(
                "Could not work out where this PC is. Add a place by hand in Settings, "
              + "or click the map to set one.");
            return;
        }

        if (_settings.Locations.Count > 0) return; // added by hand while we were waiting

        _settings.Locations.Add(new SavedLocation
        {
            Name = "Home",
            LatDeg = fix.LatDeg,
            LonDeg = fix.LonDeg,
            Source = fix.Source,
            AccuracyM = fix.AccuracyM,
            IsPrimary = true,
        });
        _settings.Save();

        ConfigureThreats();
        RebuildHomeGeometry();
        ComposeOverlay();
        EnsureStormWatchForHome();

        var site = RadarSites.Nearest(fix.LatDeg, fix.LonDeg);
        SiteCombo.SelectedItem = RadarSites.ByIcao(site.Icao);
        FilterMosaic.IsChecked = false;
        FrameSite(site);
        ApplyMode(DataMode.Live);

        // A network fix can be a town rather than a street, and the alert radius is measured
        // from this point — so say how good it is rather than implying it is exact.
        string quality = fix.AccuracyM is { } metres && metres > 5000
            ? $" That is accurate to about {Units.Distance(metres / 1000.0)}, so nudge it in "
              + "Settings if the marker is off."
            : "";
        // A notice rather than a status line: the live feed overwrites the status within
        // seconds, and this is the one thing the app did without being asked.
        ReportNotice($"Found you near {site.Icao} and saved it as Home — "
                   + $"watching for storms within {Units.Distance(_settings.AlertRadiusKm)}."
                   + $"{quality} Change it in Settings under MY PLACES.");
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
        // Configure only publishes when something changed, and "no places saved" has to reach
        // the tray on the run where nothing changed too — that is the state worth saying.
        _tray.SetStatus(_threats.Places, _threats.Current);
    }

    /// <summary>
    /// Paint the map-centre readout in the WHERE bar. Driven off the status tick rather than
    /// a camera event because it has to follow inertial panning, which settles over about a
    /// second after the mouse is released — a one-shot notification would leave the readout
    /// reporting where the drag ended rather than where the map came to rest.
    ///
    /// Centred is shown as a colour change as well as words: the whole point of the control
    /// is to be readable at a glance, and a distance that happens to say "0.4 mi" is not
    /// something the eye catches while it is looking at the weather.
    /// </summary>
    private void UpdateCentreReadout()
    {
        var camera = _mapView.Camera.Snapshot();
        var (latDeg, lonDeg) = GeoMath.FromMercator(camera.CenterX, camera.CenterY);
        var centre = CentreReadout.Describe(latDeg, lonDeg, camera.MetersPerPixel, _settings.Locations);

        CentreCoords.Text = centre.Coordinates;
        CentrePlace.Text = centre.Relation;
        CentrePlace.Visibility = centre.Relation.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        var emphasis = (System.Windows.Media.Brush)FindResource(centre.OnPlace ? "Accent" : "TextDim");
        CentreGlyph.Foreground = emphasis;
        CentrePlace.Foreground = emphasis;
        CentreReadoutBox.BorderBrush =
            (System.Windows.Media.Brush)FindResource(centre.OnPlace ? "Accent" : "Line");
        RecentreButton.Visibility =
            _settings.Primary is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Put the camera back on your place, and the radar with it.
    ///
    /// The site follows because the search box beside it already works that way — landing
    /// somewhere selects the nearest WSR-88D — and because recentring without it leaves the
    /// map over your house reading a radar that can be hundreds of miles off, which is the
    /// half-finished version of what the button says it does.
    ///
    /// The zoom does *not* reset, which is where this parts company with the search box. You
    /// press this to fix where you are looking, not how closely; throwing away the scale
    /// chosen for the storm being watched would make it a worse deal than panning back.
    /// </summary>
    private void RecentreButton_Click(object sender, RoutedEventArgs e)
    {
        // The primary place, not the nearest one the readout names: this is "put me back
        // where I live", so it has to land in the same spot every time rather than following
        // whichever saved place the camera happens to have drifted toward.
        if (_settings.Primary is not { } place) return;

        // Read the scale *before* touching the combo. Assigning SelectedItem raises
        // SelectionChanged synchronously, and its FrameSite() already moves the camera to
        // 250 m/px — so a snapshot taken after it reports 250 and "keeps the zoom" quietly
        // means "resets it". Worse, it only did so when the site actually changed, so the
        // button kept your zoom or threw it away depending on where you happened to be.
        double metresPerPixel = _mapView.Camera.Snapshot().MetersPerPixel;

        var site = RadarSites.Nearest(place.LatDeg, place.LonDeg);
        SiteCombo.SelectedItem = RadarSites.ByIcao(site.Icao);
        _mapView.Camera.MoveTo(place.LatDeg, place.LonDeg, metresPerPixel);
        UpdateCentreReadout();
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

        // The tray line says the same thing to someone with no panel to look at, so it is
        // updated on every evaluation — including the one that clears the list, which is the
        // update that says the sky is clear again.
        _tray.SetStatus(_threats.Places, threats);

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

        // A Popup owns its own HWND, so with the window hidden it is not hidden along with it:
        // it would open on the bare desktop, anchored to a MapHost that is nowhere on screen.
        // The balloon above is the entire notification in that case, which is what it is for.
        if (_inTray) return;

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

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ApplyRadarAppearance();

    private void SmoothSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ApplyRadarAppearance();

    /// <summary>
    /// Push the two radar-appearance sliders at every pane.
    ///
    /// One route for both, because they are read together in three places — the constructor's
    /// initial apply, each slider's handler, and a pane opened later — and three hand-copied
    /// versions of "value / 100" is how the renderer and the panel came to disagree in the
    /// first place. The null guard is for the handlers, which XAML raises during
    /// `InitializeComponent`, before there is a map to push anything at.
    /// </summary>
    private void ApplyRadarAppearance()
    {
        if (_mapView is null) return;

        float opacity = (float)(OpacitySlider.Value / 100.0);
        float smoothing = (float)(SmoothSlider.Value / 100.0);
        _mapView.RadarOpacity = opacity;
        _mapView.RadarSmoothing = smoothing;
        _panes?.ApplyRadarAppearance(opacity, smoothing);
    }

    /// <summary>
    /// Apply the dBZ window, and keep the two ends from crossing.
    /// </summary>
    /// <remarks>
    /// Reflectivity only. The window is in dBZ, and a dBZ bound means nothing against a
    /// velocity field or a correlation coefficient — applying it there would silently blank
    /// most of the product. Other moments get the full range, and the label says which
    /// product the control is acting on so a slider that appears to do nothing is explained.
    /// </remarks>
    private void DbzFilter_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;
        if (_mapView is null || _settings is null || DbzMinSlider is null || DbzMaxSlider is null)
            return;

        // Dragging one past the other would ask for an empty window; push the other along.
        if (sender == DbzMinSlider && DbzMinSlider.Value > DbzMaxSlider.Value)
            DbzMaxSlider.Value = DbzMinSlider.Value;
        else if (sender == DbzMaxSlider && DbzMaxSlider.Value < DbzMinSlider.Value)
            DbzMinSlider.Value = DbzMaxSlider.Value;

        _settings.DbzFilterMin = (float)DbzMinSlider.Value;
        _settings.DbzFilterMax = (float)DbzMaxSlider.Value;
        ApplyDbzFilter();

        // Not Save() here: the sliders snap every 5 dBZ, so dragging one end across the
        // range rewrites the whole settings document twenty-odd times from the UI thread.
        // The window is applied live; only writing it down waits for the drag to finish.
        MarkSettingsDirty();
    }

    /// <summary>Push the window to the renderer, or open it wide for non-reflectivity.</summary>
    private void ApplyDbzFilter()
    {
        if (_mapView is null || _settings is null || _radar is null) return;

        bool reflectivity = _radar.CurrentMoment == Moment.Reflectivity;
        if (reflectivity)
            _mapView.SetValueFilter(_settings.DbzFilterMin, _settings.DbzFilterMax);
        else
            _mapView.SetValueFilter(float.NegativeInfinity, float.PositiveInfinity);

        // Extra panes have their own MapView, and each decides for itself because they open
        // on different products.
        _panes?.ApplyValueFilter(_settings.DbzFilterMin, _settings.DbzFilterMax);

        if (DbzRangeNote is null) return;
        bool wideOpen = _settings.DbzFilterMin <= -30 && _settings.DbzFilterMax >= 75;
        // Name the cost where the control is. Snow is the case this floor gets wrong, and
        // someone watching a winter storm should not have to already know that.
        string snow = _settings.DbzFilterMin > 15
            ? " Lower it for snow."
            : "";
        DbzRangeNote.Text = !reflectivity
            ? "Reflectivity only — not applied to this product."
            : wideOpen
                ? "Showing everything."
                : $"Showing {_settings.DbzFilterMin:F0} to {_settings.DbzFilterMax:F0} dBZ.{snow}";
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
            // Same rule the captions below follow: name the depth actually used. MeanWind
            // returns what it has when asked for more, so a 4.5 km profile would otherwise
            // report a 4.5 km mean under a "0-6 km" label.
            int meanKm = (int)Math.Clamp(Math.Floor(Hodograph.DepthM(points) / 1000), 1, 6);
            motion = Hodograph.MeanWind(points, meanKm * 1000);
            basis = $"vs 0–{meanKm} km mean wind";
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

    /// <summary>
    /// Controls that persist through their own typed setting rather than the dictionaries.
    /// </summary>
    /// <remarks>
    /// The dBZ window is clamped and cross-checked on load, and the boundary layers decide
    /// whether to reach for the network, so both are worth being explicit about. Letting the
    /// generic pass also write them would give two owners for one value.
    /// </remarks>
    private static readonly HashSet<string> SeparatelyPersisted =
    [
        "DbzMinSlider", "DbzMaxSlider", "FilterStates", "FilterCounties", "FilterSites",
    ];

    /// <summary>
    /// Put the layer panel back the way it was left, then keep it that way.
    /// </summary>
    /// <remarks>
    /// Restoring a control fires its handler, which is how the state is actually applied —
    /// ticking Satellite is what starts the fetch. So the handlers are deliberately *not*
    /// suppressed; the debounce is what stops the restore from writing the file thirty times.
    ///
    /// <para>Called before <c>LoadStartupAsync</c>, which turns the national mosaic on when
    /// there is no saved place. On a genuine first run these dictionaries are empty and this
    /// does nothing, so that first-screen behaviour is untouched.</para>
    /// </remarks>
    private void RestoreLayerState()
    {
        foreach (var (name, on) in _settings.LayerToggles)
        {
            if (SeparatelyPersisted.Contains(name)) continue;
            if (FindName(name) is CheckBox box && box.IsChecked != on) box.IsChecked = on;
        }

        foreach (var (name, value) in _settings.LayerSliders)
        {
            if (SeparatelyPersisted.Contains(name)) continue;
            if (FindName(name) is Slider slider && Math.Abs(slider.Value - value) > 1e-9)
                slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        }

        foreach (var control in FindControls(LayersPanel))
        {
            switch (control)
            {
                case CheckBox box when box.Name.Length > 0 && !SeparatelyPersisted.Contains(box.Name):
                    box.Checked += LayerControl_Changed;
                    box.Unchecked += LayerControl_Changed;
                    break;
                case Slider slider when slider.Name.Length > 0 && !SeparatelyPersisted.Contains(slider.Name):
                    slider.ValueChanged += LayerSlider_Changed;
                    break;
            }
        }
    }

    private void LayerControl_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Name.Length: > 0 } box) return;
        _settings.LayerToggles[box.Name] = box.IsChecked == true;
        MarkSettingsDirty();
    }

    private void LayerSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider { Name.Length: > 0 } slider) return;
        _settings.LayerSliders[slider.Name] = slider.Value;
        MarkSettingsDirty();
    }

    /// <summary>
    /// Every checkbox and slider under <paramref name="root"/>, whether or not it is on
    /// screen.
    /// </summary>
    /// <remarks>
    /// The <em>logical</em> tree, not the visual one. A collapsed <see cref="Expander"/> has
    /// not realised its content, so a visual walk finds nothing inside Warnings, SPC or
    /// "Symbols shown" while they are shut — which is most of the time, since they ship
    /// collapsed. Their contents would then never be hooked and never persist.
    /// </remarks>
    private static IEnumerable<FrameworkElement> FindControls(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is CheckBox or Slider) yield return (FrameworkElement)node;
            foreach (var nested in FindControls(node)) yield return nested;
        }
    }

    private void PanelSection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Header: string header } section) return;
        _settings.PanelSections[header] = section.IsExpanded;
        _settings.Save();
    }

    /// <inheritdoc cref="FindControls"/>
    private static IEnumerable<Expander> FindExpanders(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is Expander expander) yield return expander;
            foreach (var nested in FindExpanders(node)) yield return nested;
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
    /// Find a storm and point everything at it: browsing site, live feed, storm overlay
    /// (temporarily overriding the home watch), camera.
    ///
    /// Plain click goes to the nearest real precipitation to your primary place, which is
    /// the one people actually want — the weather that is about to be their problem. Shift
    /// asks for the heaviest in the country instead: that is the fastest way to get real
    /// weather on screen, so it stays reachable for verifying live features. With no place
    /// saved there is no "near me" to search from, so that falls back to the same scan.
    /// </summary>
    private async void HotspotButton_Click(object sender, RoutedEventArgs e)
    {
        var origin = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? null : _settings.Primary;

        HotspotButton.IsEnabled = false;
        ShowProgress(0);
        try
        {
            Report(origin is { } place
                ? $"Scanning every radar site for the nearest storm to {place.Name}…"
                : _settings.Primary is null
                    ? "No saved place yet — scanning for the heaviest precipitation in the USA…"
                    : "Scanning every radar site for the heaviest precipitation…");

            void OnProgress(int done, int total) =>
                Dispatcher.BeginInvoke(() =>
                {
                    Report($"Scanning storm structure… {done}/{total} sites");
                    ShowProgress(done / (double)Math.Max(total, 1));
                });

            var hotspot = origin is null
                ? await NationalStormScan.FindHeaviestAsync(OnProgress)
                : await NationalStormScan.FindNearestAsync(origin.LatDeg, origin.LonDeg, OnProgress);
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

            // Distance from the place beats distance from the radar when there is a place:
            // "18 mi from Home" is the answer to what was asked, "64 mi out" is trivia.
            var distance = hotspot.DistanceKm is { } km
                ? $"{Units.Distance(km)} from {origin!.Name}"
                : $"{Units.Distance(hotspot.RangeKm)} out";
            Report($"🎯 Hotspot: VIL {hotspot.MaxVilKgM2:F0} kg/m² near {hotspot.Site.Icao} " +
                   $"({hotspot.Site.Name}, {hotspot.Site.State}), {distance} — " +
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

        if (dialog.StartupWarning is { } startupWarning) ReportError(startupWarning);

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
