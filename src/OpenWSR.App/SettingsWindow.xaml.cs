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
        ContactBox.TextChanged += (_, _) => UpdatePreview();
        UpdateKeyEnabled();
        UpdatePreview();
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
        _settings.Save();
        Units.System = _settings.Units;
        DialogResult = true;
    }
}
