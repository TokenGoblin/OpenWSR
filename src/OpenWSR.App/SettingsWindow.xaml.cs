using System.Windows;

namespace OpenWSR.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    // Home is edited on a copy and written back only in Save_Click. Every other control here
    // is read on save, so Cancel undoes it; home was not, and "Use my location" made that
    // visible — it moved the real home the moment it succeeded, and Cancel left it moved,
    // waiting for the next unrelated Save() to commit it to disk.
    private double? _homeLatDeg, _homeLonDeg, _homeAccuracyM;
    private string? _homeSource;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _settings = settings;
        (_homeLatDeg, _homeLonDeg) = (settings.HomeLatDeg, settings.HomeLonDeg);
        (_homeSource, _homeAccuracyM) = (settings.HomeSource, settings.HomeAccuracyM);

        ProviderCombo.SelectedIndex = settings.TileProvider == "maptiler" ? 1 : 0;
        KeyBox.Text = settings.MapTilerKey ?? "";
        ContactBox.Text = settings.Contact;
        UnitsCombo.SelectedIndex = settings.Units switch
        {
            UnitSystem.Metric => 1,
            UnitSystem.Nautical => 2,
            _ => 0,
        };
        LoopFramesCombo.SelectedIndex = settings.LoopFrames switch
        {
            <= 12 => 0,
            <= 30 => 1,
            <= 60 => 2,
            _ => 3,
        };

        RadiusCombo.SelectedIndex = settings.AlertRadiusKm switch
        {
            <= 15 => 0, <= 40 => 1, <= 80 => 2, _ => 3,
        };
        DirectHitCombo.SelectedIndex = settings.DirectHitRadiusKm switch
        {
            <= 2 => 0, <= 8 => 1, <= 16 => 2, _ => 3,
        };
        UpdateHomeLabel();

        ContactBox.TextChanged += (_, _) => UpdatePreview();
        UpdateKeyEnabled();
        UpdatePreview();
    }

    /// <summary>Set when the user asked to place home by clicking the map.</summary>
    public bool WantsHomePicker { get; private set; }

    /// <summary>
    /// Set when the user asked to import a colour table. The file picker is opened by the
    /// main window rather than here, because the table applies to whichever product is
    /// showing and this dialog has no business knowing which that is.
    /// </summary>
    public bool WantsPaletteImport { get; private set; }

    private void UpdateHomeLabel()
    {
        if (_homeLatDeg is { } lat && _homeLonDeg is { } lon)
        {
            string provenance = _homeSource switch
            {
                null or "" or "map" => "",
                var source when _homeAccuracyM is { } metres =>
                    $"  (from {source}, \u00B1{Units.ShortDistance(metres)})",
                var source => $"  (from {source})",
            };
            HomeLabel.Text = $"Home: {lat:F4}, {lon:F4}{provenance}";
        }
        else
        {
            HomeLabel.Text = "Home: not set";
        }
        ClearHomeButton.IsEnabled = _homeLatDeg is not null;
    }

    /// <summary>
    /// Ask Windows where we are. The await keeps the dispatcher pumping, which the consent
    /// dialog needs, and the button is disabled meanwhile so a second click cannot stack a
    /// second request behind the first one's timeout.
    /// </summary>
    private async void LocateHome_Click(object sender, RoutedEventArgs e)
    {
        LocateHomeButton.IsEnabled = false;
        ShowLocateStatus("Asking Windows for your location\u2026", error: false);
        try
        {
            var fix = await GeoLocationService.GetCurrentAsync();
            _homeLatDeg = fix.LatDeg;
            _homeLonDeg = fix.LonDeg;
            _homeSource = fix.Source;
            _homeAccuracyM = fix.AccuracyM;
            UpdateHomeLabel();
            ShowLocateStatus(
                fix.AccuracyM is { } metres && metres > 5000
                    ? $"Located to within {Units.Distance(metres / 1000.0)} \u2014 that is a coarse " +
                      "fix, so check the marker on the map and nudge it with \u201CPick on map\u201D if it is off."
                    : "Located. Save to arm proximity alerts here.",
                error: false);
        }
        catch (GeoLocationService.LocationUnavailableException ex)
        {
            ShowLocateStatus(ex.Message, error: true);
        }
        finally
        {
            LocateHomeButton.IsEnabled = true;
        }
    }

    private void ShowLocateStatus(string text, bool error)
    {
        LocateStatus.Text = text;
        // The Hint style's dim grey, restated: setting Foreground on the element beats the
        // style setter, so it has to be put back explicitly when a message stops being an error.
        LocateStatus.Foreground = error
            ? System.Windows.Media.Brushes.Salmon
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x8B, 0x93, 0xA3));
        LocateStatus.Visibility = Visibility.Visible;
    }

    private void PickHome_Click(object sender, RoutedEventArgs e)
    {
        WantsHomePicker = true;
        Save_Click(sender, e); // save the rest, then hand back to the map
    }

    private void Palette_Click(object sender, RoutedEventArgs e)
    {
        WantsPaletteImport = true;
        Save_Click(sender, e); // save the rest, then hand back to the map
    }

    private void ClearHome_Click(object sender, RoutedEventArgs e)
    {
        _homeLatDeg = _homeLonDeg = _homeAccuracyM = null;
        _homeSource = null;
        LocateStatus.Visibility = Visibility.Collapsed;
        UpdateHomeLabel();
    }

    private void UpdatePreview() =>
        UaPreview.Text = "Sends: " + (string.IsNullOrWhiteSpace(ContactBox.Text)
            ? "OpenWSR/0.1   (no contact — not recommended)"
            : $"OpenWSR/0.1 ({ContactBox.Text.Trim()})");

    private void UpdateKeyEnabled()
    {
        bool mapTiler = ProviderCombo.SelectedIndex == 1;
        if (KeyBox is not null) KeyBox.IsEnabled = mapTiler;
        if (KeyLabel is not null) KeyLabel.Opacity = mapTiler ? 1.0 : 0.5;
    }

    private void ProviderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateKeyEnabled();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.TileProvider = ProviderCombo.SelectedIndex == 1 ? "maptiler" : "osm";
        _settings.MapTilerKey = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim();
        _settings.Contact = ContactBox.Text.Trim();
        _settings.Units = UnitsCombo.SelectedIndex switch
        {
            1 => UnitSystem.Metric,
            2 => UnitSystem.Nautical,
            _ => UnitSystem.Imperial,
        };
        _settings.LoopFrames = LoopFramesCombo.SelectedIndex switch
        {
            0 => 12,
            1 => 30,
            2 => 60,
            _ => ArchivePlaybackController.MaxLoopFrames,
        };

        _settings.AlertRadiusKm = RadiusCombo.SelectedIndex switch
        {
            0 => 15, 2 => 80, 3 => 160, _ => 40,
        };
        // The last option means "no distinction": everything inside the alert radius counts
        // as a direct hit, which is what this did before the tiering existed.
        _settings.DirectHitRadiusKm = DirectHitCombo.SelectedIndex switch
        {
            0 => 2, 2 => 16, 3 => _settings.AlertRadiusKm, _ => 8,
        };
        _settings.HomeLatDeg = _homeLatDeg;
        _settings.HomeLonDeg = _homeLonDeg;
        _settings.HomeSource = _homeSource;
        _settings.HomeAccuracyM = _homeAccuracyM;
        _settings.Save();
        Units.System = _settings.Units;
        DialogResult = true;
    }
}
