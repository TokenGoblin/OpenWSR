using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenWSR.Ingest;

namespace OpenWSR.App;

/// <summary>
/// The forecast page: what it is doing outside right now, and what the next week holds.
///
/// This is the one screen in the app that is not about radar. The map answers "what is
/// happening", in reflectivity, over ground — and it cannot answer "will I need a coat
/// tomorrow", which is the question people actually open a weather app for. Both halves come
/// from api.weather.gov, keyless, the same source and User-Agent rule as the warnings poll.
///
/// Current conditions are a *station* reading, not model output, and the page says which
/// station and how far away it is. That distinction is the whole honesty of the top card:
/// the temperature is real and it was measured somewhere that is not your house.
/// </summary>
public partial class ForecastWindow : Window
{
    /// <summary>
    /// Long enough that leaving the page open costs almost nothing, short enough that it is
    /// not showing yesterday. The NWS regenerates a gridpoint forecast about hourly and an
    /// ASOS reports every twenty minutes, so anything faster re-fetches the same bytes.
    /// </summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);

    private readonly ForecastClient _client;

    /// <summary>
    /// Personal stations, or null when no Weather Underground key is set — which is the
    /// default and the whole app works without it. Built per load rather than per window, so
    /// a key added in Settings takes effect on the next refresh.
    /// </summary>
    private PwsClient? _pws;

    /// <summary>The key <see cref="_pws"/> was built with, so an edited one is noticed.</summary>
    private string? _pwsKey;

    /// <summary>
    /// The user's own station, or null without both Ambient keys. Separate from
    /// <see cref="_pws"/> because they answer different questions: that one finds stations
    /// near a point, this one reads the station the account owns.
    /// </summary>
    private AmbientClient? _ambient;

    /// <summary>The key pair <see cref="_ambient"/> was built with.</summary>
    private (string?, string?) _ambientKeys;

    private readonly AppSettings _settings;
    private readonly double _latDeg;
    private readonly double _lonDeg;
    private readonly string _placeLabel;

    /// <summary>
    /// A clock that draws rather than watches, so it stops with the window — see the tray
    /// rules in CLAUDE.md. The window itself is closed when the app hides, which is what
    /// keeps this off the list of things a hidden OpenWSR is still doing.
    /// </summary>
    private readonly DispatcherTimer _refresh;

    private CancellationTokenSource? _inFlight;
    private LocalForecast? _forecast;

    /// <summary>
    /// Set the moment the window closes, and checked by everything that resumes after an
    /// await. <c>Closed</c> runs synchronously inside <c>Close()</c>, so a fetch still in
    /// flight comes back afterwards and runs the rest of its method against a dead window —
    /// which is how the refresh clock came to be started on one.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// Set while the station combo is being repopulated. Its <c>SelectionChanged</c> is what
    /// re-reads a station, and filling the list would otherwise fire it once per item.
    /// </summary>
    private bool _fillingStations;

    /// <summary>
    /// The pinned station for *this* load, taken from settings at the start of it and set to
    /// null by whichever merge finds it is no longer there.
    ///
    /// <para>It exists because a pin that cannot be resolved used to suppress every automatic
    /// pick for ever. A dead Weather Underground pin took neither branch of its own merge and
    /// then, being non-null, stopped the Ambient merge preferring the station in your garden —
    /// silently, permanently, and across launches, with nothing on screen to explain it.</para>
    ///
    /// <para>Cleared here rather than in settings on purpose. A station can drop out of a
    /// nearby list for a scan or two without being gone, so discarding the user's choice on one
    /// miss would be worse than ignoring it for one load; this way the pin self-heals if the
    /// station comes back, and until then the automatic pick behaves as though there were
    /// none — which is what <c>AppSettings.ObservationStationId</c> already promises.</para>
    /// </summary>
    private (string Id, StationSource Source)? _pin;

    public ForecastWindow(AppSettings settings, double latDeg, double lonDeg, string placeLabel)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        _settings = settings;
        _latDeg = latDeg;
        _lonDeg = lonDeg;
        _placeLabel = placeLabel;
        _client = new ForecastClient(settings.UserAgent);

        PlaceText.Text = placeLabel;
        Title = $"Forecast — {placeLabel}";

        _refresh = new DispatcherTimer { Interval = RefreshEvery };
        _refresh.Tick += (_, _) => _ = LoadAsync();

        Loaded += async (_, _) =>
        {
            await LoadAsync();
            // Not unconditional. Closing during that first fetch runs Closed — which stops a
            // timer that has not started — and then this line resumes and starts it on a
            // window nobody can see or close again. A DispatcherTimer belongs to the
            // dispatcher rather than to the window, so it would tick every ten minutes for
            // the life of the process, holding the window alive and fetching through a
            // disposed client. It is the exact clock HideToTray closes this window to stop.
            if (!_closed) _refresh.Start();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _refresh.Stop();
            _inFlight?.Cancel();
            _client.Dispose();
            _pws?.Dispose();
            _ambient?.Dispose();
        };
    }

    // ---- fetching ----

    private async Task LoadAsync()
    {
        var previous = _inFlight;
        previous?.Cancel();
        var cts = new CancellationTokenSource();
        _inFlight = cts;
        previous?.Dispose();

        RefreshButton.IsEnabled = false;
        ShowStatus("Fetching the forecast…", failed: false);
        try
        {
            SyncPwsClient();
            SyncAmbientClient();

            _pin = _settings.ObservationStationId is { } pinnedId
                ? (pinnedId, _settings.ObservationStationSource)
                : null;

            // The NWS half is fetched with no preferred station when a personal one is pinned,
            // so its own walk out to the sixth-nearest is not spent looking for an id that is
            // not in its list.
            var forecast = await _client.GetAsync(
                _latDeg, _lonDeg,
                _pin is { Source: StationSource.Nws } nwsPin ? nwsPin.Id : null,
                cts.Token);
            if (cts.IsCancellationRequested || _closed) return;

            // Caught here rather than around the whole method, and that placement is the
            // point: a rejected key must cost the personal stations and nothing else. These
            // keys expire — so the ordinary case is a key that worked for months, and letting
            // it take the NWS forecast down with it would turn a stale credential into an app
            // with no forecast at all.
            // Each network is guarded separately, not both under one try: sharing a catch
            // would let a stale Weather Underground key suppress the station in your own
            // garden, which has nothing to do with it. Two problems at once is possible and
            // both get said.
            var keyProblems = new List<string>();
            (forecast, var pwsProblem) = await TryMergeAsync(
                forecast, "Weather Underground", MergePersonalStationsAsync, cts.Token);
            if (pwsProblem is not null) keyProblems.Add(pwsProblem);

            (forecast, var ownProblem) = await TryMergeAsync(
                forecast, "Ambient Weather", MergeOwnStationAsync, cts.Token);
            if (ownProblem is not null) keyProblems.Add(ownProblem);

            var keyProblem = keyProblems.Count > 0 ? string.Join("  ", keyProblems) : null;
            if (cts.IsCancellationRequested || _closed) return;

            _forecast = forecast;
            Draw(forecast);

            if (keyProblem is not null) ShowStatus(keyProblem, failed: true);
            else HideStatus();
        }
        // Only *our* cancellation is uninteresting — a refresh overtaken by another one, or
        // the window closing. The filter is what makes this safe: HttpClient reports its own
        // timeout as a TaskCanceledException, which is an OperationCanceledException, so an
        // unfiltered catch here swallowed a stalled request and left the page reading
        // "Fetching the forecast…" for ever with nothing saying to try again.
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (OutsideForecastAreaException e)
        {
            // Not a failure and not phrased as one: there is no forecast for this point, and
            // no amount of retrying or checking the network will produce one.
            if (_closed) return;
            ShowStatus(e.Message, failed: true);
        }
        catch (Exception e)
        {
            if (_closed) return;
            // The failure has to say what to do about it. "No forecast" and "no internet"
            // look identical on screen otherwise, and only one of them is worth waiting out.
            ShowStatus(
                $"Could not reach the National Weather Service: {e.Message} "
                + "The forecast needs an internet connection; the radar does too, so if the "
                + "map is live this is api.weather.gov rather than your network.",
                failed: true);
            Serilog.Log.Warning(e, "Forecast fetch failed for {Place}", _placeLabel);
        }
        finally
        {
            if (!cts.IsCancellationRequested && !_closed) RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Build, rebuild or drop the personal-station client to match the key in settings, so a
    /// key pasted into Settings while this window is open takes effect on the next refresh
    /// rather than on the next launch.
    /// </summary>
    private void SyncPwsClient()
    {
        var key = _settings.WeatherUndergroundKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            _pws?.Dispose();
            _pws = null;
            return;
        }
        if (_pws is not null && _pwsKey == key) return;

        _pws?.Dispose();
        _pws = new PwsClient(key, _settings.UserAgent);
        _pwsKey = key;
    }

    /// <summary>
    /// The same for the user's own station. Both keys or neither: an application key
    /// without an API key reads nobody's devices, and sending one alone only earns a
    /// rejection naming the other.
    /// </summary>
    private void SyncAmbientClient()
    {
        if (!_settings.HasAmbientKeys)
        {
            _ambient?.Dispose();
            _ambient = null;
            return;
        }

        var keys = (_settings.AmbientApplicationKey, _settings.AmbientApiKey);
        if (_ambient is not null && _ambientKeys == keys) return;

        _ambient?.Dispose();
        _ambient = new AmbientClient(keys.Item1!, keys.Item2!, _settings.UserAgent);
        _ambientKeys = keys;
    }

    /// <summary>
    /// Fold personal stations into the list and, unless the user has pinned one, re-pick the
    /// station to read.
    ///
    /// A personal station is chosen over an NWS one when it is nearer, which it almost always
    /// is — that is the entire reason for the key. What keeps it honest is that the choice is
    /// visible: the picker marks each entry with its network and the card says which kind it
    /// is showing, so "closer" never quietly becomes "official".
    /// </summary>
    private async Task<LocalForecast> MergePersonalStationsAsync(
        LocalForecast forecast, CancellationToken ct)
    {
        if (_pws is null) return forecast;

        var personal = await _pws.NearbyAsync(_latDeg, _lonDeg, ct);
        if (personal.Count == 0) return forecast;

        var stations = forecast.Stations.Concat(personal)
            .OrderBy(s => s.DistanceKm)
            .ToList();

        var current = forecast.Current;
        if (_pin is { Source: StationSource.Personal } pinned)
        {
            if (personal.FirstOrDefault(s => s.Id == pinned.Id) is { } chosen)
                current = await _pws.GetObservationAsync(chosen, ct) ?? current;
            else
                // Gone from the list. Release it for this load so the automatic pick — and
                // the Ambient merge after it — behave as though nothing were pinned.
                _pin = null;
        }

        if (_pin is null)
        {
            // Nothing pinned, so the rule is the same as the NWS half's: the nearest station
            // that is actually reporting. Personal stations go offline more often than an
            // ASOS, and one that failed its own quality check is skipped outright.
            foreach (var station in personal.Where(s => s.PassedQualityCheck != false).Take(4))
            {
                if (current is not null && station.DistanceKm >= current.Station.DistanceKm) break;

                var observation = await _pws.GetObservationAsync(station, ct);
                if (observation is { TemperatureC: not null })
                {
                    current = observation;
                    break;
                }
            }
        }

        return forecast with { Stations = stations, Current = current };
    }

    /// <summary>
    /// Fetch again from outside — what Settings calls when the Weather Underground key
    /// changes, so a key pasted with this page open takes effect at once. Without it the page
    /// keeps the station list it was built with, and a key that is working looks like one
    /// that is not.
    /// </summary>
    public void Reload()
    {
        if (!_closed) _ = LoadAsync();
    }

    /// <summary>
    /// Run one merge, keeping the forecast whatever that network does. A station network
    /// failing costs its own network and nothing else — not the forecast, and not the other
    /// network.
    ///
    /// <para>Catching only <see cref="StationAuthException"/> was too narrow to deliver that,
    /// and the gap was the interesting half: a rejected key is the *rare* case, while a
    /// timeout to api.weather.com, a 404 from the nearby lookup, or a body that does not parse
    /// are the ordinary ones — and every one of them escaped to the general handler, which
    /// returns before the forecast is drawn. The result was a blank page blaming the National
    /// Weather Service for a third party's outage, having already fetched the forecast
    /// successfully.</para>
    ///
    /// <para>Cancellation is excluded deliberately: it is the one exception here that is not
    /// this network's problem to absorb, and swallowing it would let a superseded refresh
    /// carry on drawing into the window.</para>
    /// </summary>
    private static async Task<(LocalForecast Forecast, string? Problem)> TryMergeAsync(
        LocalForecast forecast,
        string network,
        Func<LocalForecast, CancellationToken, Task<LocalForecast>> merge,
        CancellationToken ct)
    {
        try
        {
            return (await merge(forecast, ct), null);
        }
        catch (StationAuthException e)
        {
            return (forecast, e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Serilog.Log.Warning(e, "{Network} stations unavailable", network);
            return (forecast, $"{network} could not be reached, so its stations are missing "
                            + "from the list below. The forecast and the official stations are "
                            + "unaffected.");
        }
    }

    /// <summary>
    /// Fold in the station the user owns, and prefer it over everything else.
    ///
    /// Preference is not about distance here, which is why this is not simply another entry in
    /// the nearest-first merge. A neighbour's station wins by being closer; your own wins
    /// because it is the actual ground the forecast is for and because you know where it sits.
    /// It is still only preferred while it is *reporting* — a station off its batteries for a
    /// week must not outrank a working airport.
    /// </summary>
    private async Task<LocalForecast> MergeOwnStationAsync(
        LocalForecast forecast, CancellationToken ct)
    {
        if (_ambient is null) return forecast;

        var devices = await _ambient.GetDevicesAsync(_latDeg, _lonDeg, ct);
        if (devices.Count == 0) return forecast;

        var stations = devices.Select(d => d.Station)
            .Concat(forecast.Stations)
            .OrderBy(s => s.Source == StationSource.Own ? 0 : 1)
            .ThenBy(s => s.DistanceKm)
            .ToList();

        var current = forecast.Current;

        // A pin is a pin, whichever network it names — until it names a station that is not
        // there any more, at which point it is released for this load like any other.
        if (_pin is { Source: StationSource.Own } pinned)
        {
            if (devices.FirstOrDefault(d => d.Station.Id == pinned.Id) is { } chosen)
                current = chosen;
            else
                _pin = null;
        }

        if (_pin is null)
        {
            var reporting = devices.FirstOrDefault(
                d => d.TemperatureC is not null && d.Age < TimeSpan.FromHours(2));
            if (reporting is not null) current = reporting;
        }

        return forecast with { Stations = stations, Current = current };
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Re-read one station. The forecast itself does not change with the station — it is a
    /// gridpoint product and the grid has not moved — so only the top card is redrawn, and
    /// the choice is remembered.
    /// </summary>
    private async void StationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingStations || _forecast is null) return;
        if (StationCombo.SelectedItem is not ComboBoxItem { Tag: ObservationStation station }) return;

        _settings.ObservationStationId = station.Id;
        _settings.ObservationStationSource = station.Source;
        _settings.Save();

        // The window can close while this is outstanding, and Closed disposes the client —
        // so the request has to be cancellable and the result has to be checked before it is
        // drawn into a window that is gone.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _inFlight?.Token ?? CancellationToken.None);
        try
        {
            // Every source needs its own read. Routing anything but Personal at the NWS client
            // sent a MAC address to api.weather.gov, which 404s — so the user's own station,
            // the one the design says always wins, was the single entry in this list that
            // could not be selected.
            var current = station.Source switch
            {
                StationSource.Own when _ambient is not null =>
                    await _ambient.GetDeviceAsync(station, cts.Token),
                StationSource.Personal when _pws is not null =>
                    await _pws.GetObservationAsync(station, cts.Token),
                StationSource.Nws =>
                    await _client.GetObservationAsync(station, cts.Token),
                // A key cleared since the list was drawn. Nothing to read it with.
                _ => null,
            };
            if (_closed) return;

            if (current is null)
            {
                // Deliberately not falling back to another station: the user picked this one,
                // and quietly showing a different one would read as the picker being broken.
                CurrentCard.Visibility = Visibility.Collapsed;
                ObservedAgeText.Text = "not reporting";
                ShowStatus(
                    $"{station.Id} has no current observation. Stations go quiet — pick "
                    + "another from the list, or leave it and the nearest reporting one is "
                    + "chosen for you next time.",
                    failed: true);
                return;
            }
            DrawCurrent(current);
            HideStatus();
        }
        catch (OperationCanceledException)
        {
            // The window went away, or a full refresh overtook this. Neither is news.
        }
        catch (Exception ex)
        {
            if (_closed) return;
            ShowStatus($"Could not read {station.Id}: {ex.Message}", failed: true);
        }
        finally
        {
            cts.Dispose();
        }
    }

    // ---- drawing ----

    private void Draw(LocalForecast forecast)
    {
        // The API's own place name where it has one — it names the town, which is what a
        // forecast is for. Ours names a saved place, which may be "Home".
        if (forecast.PlaceName.Length > 0 && !forecast.PlaceName.Equals(_placeLabel, StringComparison.OrdinalIgnoreCase))
            PlaceText.Text = $"{_placeLabel} · {forecast.PlaceName}";

        OfficeText.Text = forecast.Office.Length > 0
            ? $"NWS {forecast.Office} · issued {forecast.GeneratedAt.ToLocalTime():ddd HH:mm}"
            : "";

        FillStations(forecast);
        if (forecast.Current is { } current) DrawCurrent(current);
        else
        {
            CurrentCard.Visibility = Visibility.Collapsed;
            ObservedAgeText.Text = "no station reporting nearby";
        }

        DrawDays(forecast.Days);
    }

    private void FillStations(LocalForecast forecast)
    {
        // try/finally, because the flag is a latch that suppresses the picker: left stuck
        // true by anything throwing in here, every later selection returns immediately and
        // the station picker silently stops working for the life of the window.
        _fillingStations = true;
        try
        {
            FillStationsCore(forecast);
        }
        finally
        {
            _fillingStations = false;
        }
    }

    private void FillStationsCore(LocalForecast forecast)
    {
        StationCombo.Items.Clear();
        foreach (var station in forecast.Stations)
        {
            var label = $"{station.Id} — {station.Name}";
            // The network is prefixed rather than tucked into the tooltip: a personal station
            // being nearer is exactly why it is in this list, and exactly why the reading
            // needs qualifying. Sorted together by distance, the two kinds interleave, so
            // without the marker there is nothing to tell them apart.
            var network = station.Source switch
            {
                StationSource.Own => "Mine · ",
                StationSource.Personal => "Personal · ",
                _ => "NWS · ",
            };
            StationCombo.Items.Add(new ComboBoxItem
            {
                // The distance is why anyone opens this list, so it goes in the closed
                // combo's text rather than only in the tooltip.
                Content = $"{network}{label}  ({Units.Distance(station.DistanceKm)} "
                        + $"{ThreatMonitor.CompassPoint(station.BearingDeg)})",
                Tag = station,
                ToolTip = station.Source switch
                {
                    StationSource.Own => $"{label} — your own station, read straight from your "
                        + "Ambient Weather account",
                    StationSource.Personal => $"{label} — someone else's personal weather station, "
                        + "closer than the official ones but with no calibration behind it",
                    _ => $"{label} — a National Weather Service station",
                },
            });
        }

        var chosen = forecast.Current?.Station.Id;
        for (int i = 0; i < StationCombo.Items.Count; i++)
        {
            if (StationCombo.Items[i] is ComboBoxItem { Tag: ObservationStation s } && s.Id == chosen)
            {
                StationCombo.SelectedIndex = i;
                break;
            }
        }
        StationCombo.IsEnabled = StationCombo.Items.Count > 0;
    }

    private void DrawCurrent(CurrentConditions current)
    {
        CurrentCard.Visibility = Visibility.Visible;

        CurrentGlyph.Text = SkyGlyph.For(current.Sky);
        CurrentGlyph.FontFamily = SkyGlyph.Font;
        CurrentTemp.Text = current.TemperatureC is { } t ? Units.Temperature(t) : "—";

        // A personal station has no observer and no ceilometer, so it reports numbers and
        // never a sky. Borrowing the forecast's own wording for the current period beats
        // leaving the line blank under a temperature.
        CurrentDescription.Text = current.Description.Length > 0
            ? current.Description
            : SkyGlyph.Describe(current.Sky) is { Length: > 0 } described
                ? described
                : _forecast?.Periods.FirstOrDefault()?.ShortForecast ?? "";

        // The provenance sits next to the age rather than only in the picker, because the
        // picker is closed most of the time and this is the line that qualifies the big
        // number above it.
        ObservedAgeText.Text = current.Station.Source switch
        {
            StationSource.Own => $"{Ago(current.Age)} · your station",
            StationSource.Personal => $"{Ago(current.Age)} · personal station",
            _ => Ago(current.Age),
        };

        CurrentStats.Children.Clear();
        AddStat("Feels like", current.FeelsLikeC is { } f ? Units.Temperature(f) : SameAsAir(current));
        AddStat("Dew point", current.DewpointC is { } d ? Units.Temperature(d) : "—");
        AddStat("Humidity", current.RelativeHumidityPercent is { } h ? $"{h:F0} %" : "—");
        AddStat("Wind", Wind(current));
        AddStat("Pressure", current.PressurePa is { } p ? Units.Pressure(p) : "—");
        AddStat("Visibility", current.VisibilityM is { } v ? Units.Distance(v / 1000.0) : "—");

        // Only a station at the place itself reports this, so the row appears only when
        // there is one. An em dash here would be a permanent empty cell on every setup
        // that reads an airport, which is most of them.
        if (current.PrecipitationLastHourMm is { } rain)
            AddStat("Rain, last hour", Units.Rainfall(rain));
    }

    /// <summary>
    /// No heat index and no wind chill is the normal state of a mild day, not a gap in the
    /// data — the indices are only defined at the ends of the range. Saying so beats an
    /// em dash, which would read as the station having failed to report something.
    /// </summary>
    private static string SameAsAir(CurrentConditions current) =>
        current.TemperatureC is null ? "—" : "same as air";

    private static string Wind(CurrentConditions current)
    {
        if (current.WindSpeedKmh is not { } speed) return "—";
        if (speed < 1.0) return "calm";

        var direction = current.WindDirectionDeg is { } deg
            ? $"{ThreatMonitor.CompassPoint(deg)} "
            : "";
        var gust = current.WindGustKmh is { } g && g > speed
            ? $", gusting {Units.Speed(g)}"
            : "";
        return $"{direction}{Units.Speed(speed)}{gust}";
    }

    private void AddStat(string label, string value)
    {
        var cell = new StackPanel { Margin = new Thickness(0, 0, 14, 10) };
        cell.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("TextDim"),
        });
        cell.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 14,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("Text"),
        });
        CurrentStats.Children.Add(cell);
    }

    private void DrawDays(IReadOnlyList<ForecastDay> days)
    {
        DaysPanel.Children.Clear();
        DaysHeading.Visibility = days.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var day in days)
            DaysPanel.Children.Add(DayRow(day));
    }

    /// <summary>
    /// One day: the sky, the day's name, its two temperatures, the rain chance, and the
    /// office's own prose underneath. The prose is the part worth the width — "a chance of
    /// showers after 3pm" is a different afternoon from "showers likely", and a summary row
    /// alone renders both as a percentage.
    /// </summary>
    private Border DayRow(ForecastDay day)
    {
        var face = day.Face;
        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = SkyGlyph.For(face?.Sky ?? ""),
            FontFamily = SkyGlyph.Font,
            FontSize = 26,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)FindResource("Text"),
        };
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(glyph);

        var middle = new StackPanel();
        middle.Children.Add(new TextBlock
        {
            Text = day.Label,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Text"),
        });
        middle.Children.Add(new TextBlock
        {
            Text = face?.ShortForecast ?? "",
            FontSize = 12,
            Margin = new Thickness(0, 2, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextDim"),
        });

        // The half is named only when there are two of them to tell apart. On a row that has
        // only one, the prefix repeats the heading directly above it — "Tonight" over
        // "Tonight: a chance of showers".
        bool nameTheHalves = day.Day is not null && day.Night is not null;
        foreach (var half in new[] { day.Day, day.Night })
        {
            if (half is null) continue;
            middle.Children.Add(new TextBlock
            {
                Text = nameTheHalves ? $"{half.Name}: {half.DetailedForecast}" : half.DetailedForecast,
                FontSize = 12,
                Margin = new Thickness(0, 8, 12, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextDim"),
            });
        }
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var temps = new StackPanel { Orientation = Orientation.Horizontal };
        // Both slots are always drawn so the column lines up down the week, but an absent
        // half is a placeholder and must not outrank the reading beside it: with the high
        // painted in the bright brush unconditionally, tonight's row led with a bold em dash
        // and put the one real temperature it had in the dim one.
        temps.Children.Add(new TextBlock
        {
            Text = day.HighC is { } high ? Units.Temperature(high) : "—",
            FontSize = 17,
            Foreground = (Brush)FindResource(day.HighC is null ? "TextDisabled" : "Text"),
            ToolTip = day.HighC is null ? "No daytime forecast left for today" : "Daytime high",
        });
        temps.Children.Add(new TextBlock
        {
            Text = day.LowC is { } low ? Units.Temperature(low) : "—",
            FontSize = 17,
            Margin = new Thickness(10, 0, 0, 0),
            // With no daytime half there is only one temperature on the row, and it is this
            // one; dimming it as the secondary of a pair would be a lie about which is which.
            Foreground = (Brush)FindResource(
                day.LowC is null ? "TextDisabled" : day.HighC is null ? "Text" : "TextDim"),
            ToolTip = "Overnight low",
        });
        right.Children.Add(temps);

        // Zero is a real answer and a useful one, but a row of "0 %" down the whole week is
        // noise; the chance is shown once it is worth planning around.
        int chance = Math.Max(day.Day?.PrecipitationChancePercent ?? 0,
                              day.Night?.PrecipitationChancePercent ?? 0);
        if (chance > 0)
        {
            right.Children.Add(new TextBlock
            {
                Text = $"{chance} % rain",
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = (Brush)FindResource(chance >= 50 ? "Accent" : "TextDim"),
            });
        }

        if (face is { WindSpeed.Length: > 0 })
        {
            right.Children.Add(new TextBlock
            {
                Text = $"{face.WindDirection} {face.WindSpeed}",
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = (Brush)FindResource("TextDim"),
            });
        }
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return new Border
        {
            Child = grid,
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("Surface"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
        };
    }

    // ---- status ----

    private void ShowStatus(string message, bool failed)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(failed ? "Text" : "TextDim");
        StatusBar.BorderBrush = (Brush)FindResource(failed ? "Accent" : "Line");
        StatusBar.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusBar.Visibility = Visibility.Collapsed;

    private static string Ago(TimeSpan age) => age.TotalMinutes switch
    {
        < 2 => "just now",
        < 90 => $"{age.TotalMinutes:F0} min ago",
        _ => $"{age.TotalHours:F0} h ago",
    };
}

/// <summary>
/// A NWS icon token drawn as a character rather than fetched as a picture.
///
/// The API publishes a PNG per condition, but they are light-background raster art sized for
/// a web page — against this app's near-black chrome they read as bright rectangles, and
/// each one is a network fetch that can fail on its own. The token set is small and closed,
/// so a glyph per token costs nothing and always renders.
/// </summary>
internal static class SkyGlyph
{
    /// <summary>
    /// The token comes from the icon URL. Prefixes are matched after exact names so
    /// "tsra_sct" and "tsra_hi" fall through to the thunderstorm symbol without being
    /// enumerated, and an unknown token draws a neutral cloud rather than nothing at all.
    /// </summary>
    public static string For(string sky) => sky switch
    {
        "" => "•",
        "skc" or "hot" => "☀",
        "few" or "sct" => "⛅",
        "bkn" => "🌥",
        "ovc" => "☁",
        // The wind variants draw their sky and nothing else. A wind symbol here says the
        // same thing twice — the speed is a stat six lines below — and the one glyph that
        // reads as "windy" is a face blowing, which at 46 px is an unrecognisable blob.
        "wind_skc" => "☀",
        "wind_few" or "wind_sct" => "⛅",
        "wind_bkn" => "🌥",
        "wind_ovc" => "☁",
        "rain" or "rain_showers" or "rain_showers_hi" => "🌧",
        "snow" or "blizzard" or "cold" => "❄",
        "rain_snow" or "rain_sleet" or "snow_sleet" or "sleet" => "🌨",
        "fzra" or "rain_fzra" or "snow_fzra" => "🧊",
        "fog" or "haze" or "smoke" or "dust" => "🌫",
        "tornado" => "🌪",
        "hurricane" or "tropical_storm" => "🌀",
        _ when sky.StartsWith("tsra", StringComparison.Ordinal) => "⛈",
        _ when sky.StartsWith("rain", StringComparison.Ordinal) => "🌧",
        _ when sky.StartsWith("snow", StringComparison.Ordinal) => "❄",
        _ => "☁",
    };

    /// <summary>
    /// Named explicitly rather than left to font fallback. These are emoji, and the fallback
    /// chain picks whichever installed face happens to claim the codepoint — which rendered
    /// them as mismatched monochrome line art at 46 px, one symbol in a different weight from
    /// the next.
    /// </summary>
    public static readonly System.Windows.Media.FontFamily Font =
        new("Segoe UI Emoji, Segoe UI Symbol, Segoe UI");

    /// <summary>
    /// A fallback description for a station that reports a sky but no words for it. Mesonet
    /// stations frequently send the numbers and leave <c>textDescription</c> empty.
    /// </summary>
    public static string Describe(string sky) => sky switch
    {
        "" => "",
        "skc" => "Clear",
        "few" => "Mostly clear",
        "sct" => "Partly cloudy",
        "bkn" => "Mostly cloudy",
        "ovc" => "Overcast",
        "hot" => "Hot",
        "cold" => "Cold",
        "fog" => "Fog",
        _ when sky.StartsWith("tsra", StringComparison.Ordinal) => "Thunderstorms",
        _ when sky.StartsWith("rain", StringComparison.Ordinal) => "Rain",
        _ when sky.StartsWith("snow", StringComparison.Ordinal) => "Snow",
        _ when sky.StartsWith("wind", StringComparison.Ordinal) => "Windy",
        _ => sky.Replace('_', ' '),
    };
}
