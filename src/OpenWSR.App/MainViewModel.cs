using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenWSR.Nexrad;

namespace OpenWSR.App;

/// <summary>Which source is answering "when" — the three are mutually exclusive.</summary>
public enum DataMode
{
    /// <summary>Real-time chunk streaming from the selected site.</summary>
    Live,
    /// <summary>A UTC day of archived volumes, scrubbed or looped.</summary>
    Archive,
    /// <summary>HRRR simulated reflectivity, stepped forward six hours.</summary>
    Forecast,
}

/// <summary>
/// What a drag on the map means. These used to be independent toggles, so measure and
/// cross-section were both bound to right-drag at the same time; one armed tool at a time
/// makes the gesture unambiguous and gives the status bar something honest to say.
/// </summary>
public enum MapTool
{
    /// <summary>Hover reads values; right-drag measures distance and bearing.</summary>
    Inspect,
    /// <summary>Right-drag measures, and the line stays until cleared.</summary>
    Measure,
    /// <summary>Right-drag slices the volume vertically.</summary>
    CrossSection,
    /// <summary>The next left-click sets the home location.</summary>
    SetHome,
}

/// <summary>One product button in the bar above the map.</summary>
public sealed partial class MomentOption(Moment moment, string label, string key, string description)
    : ObservableObject
{
    public Moment Moment { get; } = moment;

    /// <summary>Short product code, the way it is written on a radar console.</summary>
    public string Label { get; } = label;

    /// <summary>The keyboard shortcut, shown on the button so the shortcut is discoverable.</summary>
    public string Key { get; } = key;

    public string Description { get; } = description;

    /// <summary>False when the loaded volume carries no cut of this moment.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string Tooltip => IsAvailable
        ? $"{Description}  ({Key})"
        : $"{Description} — not in this volume";
}

/// <summary>One elevation cut of the selected moment.</summary>
public sealed record TiltOption(int Position, float ElevationDeg, int Count)
{
    public string Label => $"{ElevationDeg:F1}°";

    /// <summary>
    /// Explicit, because a record's generated ToString prints its whole shape — and the
    /// combo box falls back to it whenever the selection box has no item template.
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>
/// The state the shell is arranged around: which data mode is showing, which product and
/// tilt are selected, and which map tool is armed. It lives here rather than in control
/// properties so the layout can move a control between containers without rewriting the
/// logic that reads it.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Moments =
        [
            new MomentOption(Moment.Reflectivity, "REF", "R", "Reflectivity — how much is falling"),
            new MomentOption(Moment.Velocity, "VEL", "V", "Radial velocity — motion toward and away"),
            new MomentOption(Moment.SpectrumWidth, "SW", "W", "Spectrum width — turbulence"),
            new MomentOption(Moment.DifferentialReflectivity, "ZDR", "D", "Differential reflectivity — drop shape"),
            new MomentOption(Moment.DifferentialPhase, "PHI", "P", "Differential phase — path-integrated"),
            new MomentOption(Moment.CorrelationCoefficient, "CC", "C", "Correlation coefficient — is it all one thing"),
            new MomentOption(Moment.AzimuthalShear, "AZS", "A", "Azimuthal shear — rotation, computed from velocity"),
        ];
    }

    public IReadOnlyList<MomentOption> Moments { get; }

    public ObservableCollection<TiltOption> Tilts { get; } = [];

    [ObservableProperty]
    public partial DataMode Mode { get; set; } = DataMode.Archive;

    [ObservableProperty]
    public partial MapTool Tool { get; set; } = MapTool.Inspect;

    [ObservableProperty]
    public partial TiltOption? SelectedTilt { get; set; }

    /// <summary>Set while a tilt is being applied from code, so the combo doesn't echo back.</summary>
    public bool SuppressTiltEcho { get; set; }

    public bool IsLive => Mode == DataMode.Live;
    public bool IsArchive => Mode == DataMode.Archive;
    public bool IsForecast => Mode == DataMode.Forecast;

    partial void OnModeChanged(DataMode value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsArchive));
        OnPropertyChanged(nameof(IsForecast));
    }

    /// <summary>Mark which products the loaded volume actually carries, and which is showing.</summary>
    public void SyncMoments(IReadOnlyCollection<Moment> available, Moment selected)
    {
        foreach (var option in Moments)
        {
            option.IsAvailable = available.Contains(option.Moment);
            option.IsSelected = option.Moment == selected;
        }
    }

    /// <summary>Replace the tilt list for the current moment and select one by position.</summary>
    public void SyncTilts(IReadOnlyList<float> elevationsDeg, int position)
    {
        SuppressTiltEcho = true;
        try
        {
            Tilts.Clear();
            for (int i = 0; i < elevationsDeg.Count; i++)
                Tilts.Add(new TiltOption(i, elevationsDeg[i], elevationsDeg.Count));
            SelectedTilt = position >= 0 && position < Tilts.Count ? Tilts[position] : null;
        }
        finally
        {
            SuppressTiltEcho = false;
        }
    }

    /// <summary>What the status bar should say about the armed tool's gesture.</summary>
    public static string ToolHint(MapTool tool) => tool switch
    {
        MapTool.Measure => "Right-drag to measure distance and bearing",
        MapTool.CrossSection => "Right-drag a line to slice the storm vertically",
        MapTool.SetHome => "Click the map to set your home location",
        _ => "Hover to read values · right-drag to measure",
    };
}
