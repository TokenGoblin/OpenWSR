using OpenWSR.Nexrad;
using OpenWSR.Palettes;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// Holds the displayed volume and the current moment/elevation selection, and pushes
/// the selected sweep to the map. Product switching is a texture swap in the renderer;
/// this class just picks which sweep to stage.
/// </summary>
public sealed class RadarDisplayController(MapView mapView)
{
    private RadarVolume? _volume;
    private Moment _moment = Moment.Reflectivity;
    private int _cutPosition; // index into the ordered list of cuts carrying the moment

    public event Action<string>? StatusChanged;

    private readonly Dictionary<Moment, ColorTable> _customTables = [];

    public Moment CurrentMoment => _moment;
    public ColorTable CurrentTable =>
        _customTables.TryGetValue(_moment, out var custom) ? custom : BuiltinTables.For(_moment);

    /// <summary>Scan time of the sweep currently on screen — feeds the data-age indicator.</summary>
    public DateTime? DisplayedSweepTimeUtc { get; private set; }

    /// <summary>The sweep currently on screen — feeds the hover inspector.</summary>
    public Sweep? DisplayedSweep { get; private set; }

    /// <summary>Apply an imported .pal table to the current moment and re-render.</summary>
    public void SetCustomTable(ColorTable table)
    {
        _customTables[_moment] = table;
        Apply();
    }

    /// <summary>Re-render the current selection, e.g. after a units or palette change.</summary>
    public void Refresh() => Apply();

    public void ShowVolume(RadarVolume volume)
    {
        _volume = volume;
        Apply(); // moment and cut position survive volume changes (scrubbing/looping)
    }

    /// <summary>The sweep the current moment/tilt selection picks from an arbitrary volume.</summary>
    public Sweep? SelectSweep(RadarVolume volume)
    {
        var cuts = volume.Sweeps.Where(s => s.Moment == _moment)
            .OrderBy(s => s.ElevationIndex)
            .ToList();
        if (cuts.Count == 0) return null;
        return cuts[Math.Clamp(_cutPosition, 0, cuts.Count - 1)];
    }

    /// <summary>Map keyboard: R/V/W/D/P/C select the moment, Up/Down move through cuts.</summary>
    public void OnKey(int virtualKey)
    {
        switch (virtualKey)
        {
            case 0x52: SetMoment(Moment.Reflectivity); break;            // R
            case 0x56: SetMoment(Moment.Velocity); break;                // V
            case 0x57: SetMoment(Moment.SpectrumWidth); break;           // W
            case 0x44: SetMoment(Moment.DifferentialReflectivity); break; // D
            case 0x50: SetMoment(Moment.DifferentialPhase); break;       // P
            case 0x43: SetMoment(Moment.CorrelationCoefficient); break;  // C
            case 0x26: MoveCut(+1); break;                               // Up
            case 0x28: MoveCut(-1); break;                               // Down
        }
    }

    private void SetMoment(Moment moment)
    {
        if (_moment == moment) return;
        var currentElevation = CutsForMoment(_moment).ElementAtOrDefault(_cutPosition)?.ElevationAngleDeg;
        _moment = moment;
        var cuts = CutsForMoment(moment);
        // Stay at the nearest elevation angle when the new moment lives on different cuts.
        _cutPosition = currentElevation is null || cuts.Count == 0
            ? 0
            : cuts.IndexOf(cuts.OrderBy(s => Math.Abs(s.ElevationAngleDeg - currentElevation.Value)).First());
        Apply();
    }

    private void MoveCut(int direction)
    {
        int count = CutsForMoment(_moment).Count;
        if (count == 0) return;
        _cutPosition = Math.Clamp(_cutPosition + direction, 0, count - 1);
        Apply();
    }

    private List<Sweep> CutsForMoment(Moment moment) =>
        _volume?.Sweeps.Where(s => s.Moment == moment)
            .OrderBy(s => s.ElevationIndex)
            .ToList() ?? [];

    private void Apply()
    {
        var cuts = CutsForMoment(_moment);
        if (cuts.Count == 0)
        {
            mapView.ClearSweep();
            StatusChanged?.Invoke($"{_moment}: no data in this volume");
            return;
        }
        _cutPosition = Math.Clamp(_cutPosition, 0, cuts.Count - 1);
        var sweep = cuts[_cutPosition];
        DisplayedSweepTimeUtc = sweep.ScanTimeUtc;
        DisplayedSweep = sweep;
        mapView.ShowSweep(sweep, CurrentTable);
        StatusChanged?.Invoke(
            $"{sweep.SiteId}  {sweep.ScanTimeUtc:HH:mm:ss}Z  {_moment}  " +
            $"{sweep.ElevationAngleDeg:F1}°  (tilt {_cutPosition + 1} of {cuts.Count})");
    }
}
