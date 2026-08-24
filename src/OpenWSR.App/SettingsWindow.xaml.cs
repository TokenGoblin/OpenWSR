using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace OpenWSR.App;

/// <summary>
/// One saved place, wrapped for editing.
///
/// The list is edited on copies and written back only on Save, the same way every other
/// control in this dialog is read on save — that is what makes Cancel mean anything. Editing
/// the real <see cref="SavedLocation"/> objects in place would leave a removed place removed
/// and a renamed one renamed however the dialog was closed.
/// </summary>
public sealed class PlaceRow(SavedLocation source) : INotifyPropertyChanged
{
    public string Name { get; set; } = source.Name;
    public double LatDeg { get; } = source.LatDeg;
    public double LonDeg { get; } = source.LonDeg;
    public string? Source { get; } = source.Source;
    public double? AccuracyM { get; } = source.AccuracyM;
    public double? AlertRadiusKm { get; set; } = source.AlertRadiusKm;

    /// <summary>
    /// The radius combo's selection, both ways. Bound rather than driven by an event because
    /// an event only fires when the user touches the control — a row loaded from settings
    /// would show a blank box next to a radius it is actually using.
    /// </summary>
    public int RadiusIndex
    {
        get => AlertRadiusKm switch
        {
            null => 0, <= 15 => 1, <= 40 => 2, <= 80 => 3, _ => 4,
        };
        set => AlertRadiusKm = value switch
        {
            1 => 15, 2 => 40, 3 => 80, 4 => 160,
            _ => null,   // "Default" — fall back to the global radius
        };
    }

    private bool _isPrimary = source.IsPrimary;

    public bool IsPrimary
    {
        get => _isPrimary;
        set
        {
            if (_isPrimary == value) return;
            _isPrimary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPrimary)));
        }
    }

    /// <summary>The coordinates and where they came from, for the line under the name.</summary>
    public string Where => Source switch
    {
        null or "" or "map" => $"{LatDeg:F4}, {LonDeg:F4}",
        var s when AccuracyM is { } metres =>
            $"{LatDeg:F4}, {LonDeg:F4}  (from {s}, ±{Units.ShortDistance(metres)})",
        var s => $"{LatDeg:F4}, {LonDeg:F4}  (from {s})",
    };

    public SavedLocation ToSaved() => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Unnamed" : Name.Trim(),
        LatDeg = LatDeg,
        LonDeg = LonDeg,
        Source = Source,
        AccuracyM = AccuracyM,
        AlertRadiusKm = AlertRadiusKm,
        IsPrimary = IsPrimary,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    // Places are edited on copies and written back only in Save_Click. Every other control
    // here is read on save, so Cancel undoes it; the single home this replaced was not, and
    // "Use my location" made that visible — it moved the real home the moment it succeeded,
    // and Cancel left it moved, waiting for the next unrelated Save() to commit it to disk.
    private readonly ObservableCollection<PlaceRow> _places = [];

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _settings = settings;

        foreach (var location in settings.Locations) _places.Add(new PlaceRow(location));
        PlaceList.ItemsSource = _places;
        _places.CollectionChanged += (_, _) => SyncPlaceList();

        ProviderCombo.SelectedIndex = settings.TileProvider switch
        {
            "carto-dark" => 0,
            "maptiler" => 2,
            _ => 1,
        };
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
        SyncPlaceList();

        ContactBox.TextChanged += (_, _) => UpdatePreview();
        UpdateKeyEnabled();
        UpdatePreview();
    }

    /// <summary>Set when the user asked to move the primary place by clicking the map.</summary>
    public bool WantsHomePicker { get; private set; }

    /// <summary>
    /// Set when the user asked to import a colour table. The file picker is opened by the
    /// main window rather than here, because the table applies to whichever product is
    /// showing and this dialog has no business knowing which that is.
    /// </summary>
    public bool WantsPaletteImport { get; private set; }

    private void SyncPlaceList()
    {
        NoPlacesLabel.Visibility = _places.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // "Move primary on map" needs a primary to move.
        PickHomeButton.Content = _places.Count == 0 ? "Add by clicking the map" : "Move primary on map";
    }

    /// <summary>
    /// Exactly one primary. The radio group enforces it among the buttons, but the collection
    /// has to be told too, or the flag that gets saved disagrees with what is on screen.
    /// </summary>
    private void Primary_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PlaceRow chosen }) return;
        foreach (var row in _places) row.IsPrimary = ReferenceEquals(row, chosen);
    }

    private void RemovePlace_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PlaceRow row }) return;
        bool wasPrimary = row.IsPrimary;
        _places.Remove(row);
        // Something has to be primary while anything remains, or the app has no place to
        // open on and no radar to follow.
        if (wasPrimary && _places.Count > 0) _places[0].IsPrimary = true;
    }

    /// <summary>
    /// Ask Windows where we are and add it as a place. The await keeps the dispatcher pumping,
    /// which the consent dialog needs, and the button is disabled meanwhile so a second click
    /// cannot stack a second request behind the first one's timeout.
    /// </summary>
    private async void LocateHome_Click(object sender, RoutedEventArgs e)
    {
        LocateHomeButton.IsEnabled = false;
        ShowLocateStatus("Asking Windows for your location…", error: false);
        try
        {
            var fix = await GeoLocationService.GetCurrentAsync();
            _places.Add(new PlaceRow(new SavedLocation
            {
                Name = _places.Count == 0 ? "Home" : "New place",
                LatDeg = fix.LatDeg,
                LonDeg = fix.LonDeg,
                Source = fix.Source,
                AccuracyM = fix.AccuracyM,
                IsPrimary = _places.Count == 0,
            }));

            ShowLocateStatus(
                fix.AccuracyM is { } metres && metres > 5000
                    ? $"Added, to within {Units.Distance(metres / 1000.0)} — that is a coarse " +
                      "fix, so check the marker on the map and nudge it with “Move primary on map” if it is off."
                    : "Added. Name it, then save to arm proximity alerts there.",
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

    private void UpdatePreview() =>
        UaPreview.Text = "Sends: " + (string.IsNullOrWhiteSpace(ContactBox.Text)
            ? "OpenWSR/0.1   (no contact — not recommended)"
            : $"OpenWSR/0.1 ({ContactBox.Text.Trim()})");

    private void UpdateKeyEnabled()
    {
        bool mapTiler = ProviderCombo.SelectedIndex == 2;
        if (KeyBox is not null) KeyBox.IsEnabled = mapTiler;
        if (KeyLabel is not null) KeyLabel.Opacity = mapTiler ? 1.0 : 0.5;
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateKeyEnabled();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.TileProvider = ProviderCombo.SelectedIndex switch
        {
            0 => "carto-dark",
            2 => "maptiler",
            _ => "osm",
        };
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

        _settings.Locations = [.. _places.Select(p => p.ToSaved())];
        // A list edited down to nothing primary would leave the app with nowhere to open.
        if (_settings.Locations.Count > 0 && !_settings.Locations.Any(l => l.IsPrimary))
            _settings.Locations[0].IsPrimary = true;

        _settings.Save();
        Units.System = _settings.Units;
        DialogResult = true;
    }
}
