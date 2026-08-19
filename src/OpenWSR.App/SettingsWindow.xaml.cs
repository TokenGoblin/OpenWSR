using System.Windows;

namespace OpenWSR.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _settings = settings;

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
        UpdateHomeLabel();

        ContactBox.TextChanged += (_, _) => UpdatePreview();
        UpdateKeyEnabled();
        UpdatePreview();
    }

    /// <summary>Set when the user asked to place home by clicking the map.</summary>
    public bool WantsHomePicker { get; private set; }

    private void UpdateHomeLabel()
    {
        HomeLabel.Text = _settings.HomeLatDeg is { } lat && _settings.HomeLonDeg is { } lon
            ? $"Home: {lat:F3}, {lon:F3}"
            : "Home: not set";
        ClearHomeButton.IsEnabled = _settings.HomeLatDeg is not null;
    }

    private void PickHome_Click(object sender, RoutedEventArgs e)
    {
        WantsHomePicker = true;
        Save_Click(sender, e); // save the rest, then hand back to the map
    }

    private void ClearHome_Click(object sender, RoutedEventArgs e)
    {
        _settings.HomeLatDeg = null;
        _settings.HomeLonDeg = null;
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
        _settings.Save();
        Units.System = _settings.Units;
        DialogResult = true;
    }
}
