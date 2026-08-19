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

    /// <summary>
    /// The moment or tilt on screen changed. The product bar renders from this rather than
    /// from whatever last called a setter, so keyboard and mouse stay in step.
    /// </summary>
    public event Action? SelectionChanged;

    private readonly Dictionary<Moment, ColorTable> _customTables = [];

    public Moment CurrentMoment => _moment;

    /// <summary>Which products the loaded volume actually carries — the rest grey out.</summary>
    public IReadOnlyCollection<Moment> AvailableMoments =>
        _volume is null ? [] : _volume.Sweeps.Select(s => s.Moment).Distinct().ToHashSet();

    /// <summary>Elevation angles of the current moment's cuts, in scan order.</summary>
    public IReadOnlyList<float> ElevationsForCurrentMoment =>
        [.. CutsForMoment(_moment).Select(s => s.ElevationAngleDeg)];

    /// <summary>Index into <see cref="ElevationsForCurrentMoment"/> of the displayed cut.</summary>
    public int CutPosition => _cutPosition;

    /// <summary>Show a product. No-op when it is already showing.</summary>
    public void SelectMoment(Moment moment) => SetMoment(moment);

    /// <summary>Show a specific cut of the current product.</summary>
    public void SelectCut(int position)
    {
        int count = CutsForMoment(_moment).Count;
        if (count == 0) return;
        int clamped = Math.Clamp(position, 0, count - 1);
        if (clamped == _cutPosition) return;
        _cutPosition = clamped;
        Apply();
    }

    /// <summary>Step up or down through the cuts of the current product.</summary>
    public void StepCut(int direction) => MoveCut(direction);
    public ColorTable CurrentTable =>
        _customTables.TryGetValue(_moment, out var custom) ? custom : BuiltinTables.For(_moment);

    /// <summary>Scan time of the sweep currently on screen — feeds the data-age indicator.</summary>
    public DateTime? DisplayedSweepTimeUtc { get; private set; }

    /// <summary>The sweep currently on screen — feeds the hover inspector.</summary>
    public Sweep? DisplayedSweep { get; private set; }

    /// <summary>Every sweep of the current moment, for analysis across the whole volume.</summary>
    public IReadOnlyList<Sweep> SweepsForCurrentMoment() => CutsForMoment(_moment);

    /// <summary>Apply an imported .pal table to the current moment and re-render.</summary>
    public void SetCustomTable(ColorTable table)
    {
        _customTables[_moment] = table;
        Apply();
    }

    /// <summary>Re-render the current selection, e.g. after a units or palette change.</summary>
    public void Refresh() => Apply();

    /// <summary>Subtract storm motion from velocity, so rotation stands out from translation.</summary>
    public bool StormRelative { get; set; }

    /// <summary>Storm motion used for the subtraction: speed in km/h and bearing in degrees.</summary>
    public (double SpeedKmh, double BearingDeg) StormMotion { get; set; }

    /// <summary>
    /// Storm-relative velocity: remove the component of the cell's own movement along
    /// each radial. A squall line translating at 50 km/h swamps the rotation signature
    /// in raw velocity; taking the motion out leaves the couplet visible.
    /// </summary>
    private static Sweep ToStormRelative(Sweep sweep, double speedKmh, double bearingDeg)
    {
        double speed = speedKmh / 3.6; // m/s
        double bearing = bearingDeg * Math.PI / 180.0;
        double u = speed * Math.Sin(bearing); // east
        double v = speed * Math.Cos(bearing); // north

        var data = new float[sweep.Data.Length];
        for (int radial = 0; radial < sweep.RadialCount; radial++)
        {
            double azimuth = sweep.AzimuthsDeg[radial] * Math.PI / 180.0;
            float alongBeam = (float)(u * Math.Sin(azimuth) + v * Math.Cos(azimuth));
            int offset = radial * sweep.GateCount;
            for (int gate = 0; gate < sweep.GateCount; gate++)
            {
                float value = sweep.Data[offset + gate];
                data[offset + gate] = float.IsNaN(value) ? value : value - alongBeam;
            }
        }
        return sweep with { Data = data };
    }

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

    /// <summary>
    /// Map keyboard: R/V/W/D/P/C select the moment, Up/Down move through cuts. The single
    /// place these are interpreted — it used to be wired from both the map's child window
    /// and the WPF window, which double-stepped the tilt and let the search box change the
    /// product. Returns true when the key was one of ours.
    /// </summary>
    public bool OnKey(int virtualKey)
    {
        switch (virtualKey)
        {
            case 0x52: SetMoment(Moment.Reflectivity); return true;             // R
            case 0x56: SetMoment(Moment.Velocity); return true;                 // V
            case 0x57: SetMoment(Moment.SpectrumWidth); return true;            // W
            case 0x44: SetMoment(Moment.DifferentialReflectivity); return true; // D
            case 0x50: SetMoment(Moment.DifferentialPhase); return true;        // P
            case 0x43: SetMoment(Moment.CorrelationCoefficient); return true;   // C
            case 0x26: MoveCut(+1); return true;                                // Up
            case 0x28: MoveCut(-1); return true;                                // Down
            default: return false;
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
            DisplayedSweep = null;
            DisplayedSweepTimeUtc = null;
            SelectionChanged?.Invoke();
            StatusChanged?.Invoke($"{_moment}: no data in this volume");
            return;
        }
        _cutPosition = Math.Clamp(_cutPosition, 0, cuts.Count - 1);
        var sweep = cuts[_cutPosition];

        bool relative = StormRelative && _moment == Moment.Velocity && StormMotion.SpeedKmh > 0;
        if (relative)
            sweep = ToStormRelative(sweep, StormMotion.SpeedKmh, StormMotion.BearingDeg);

        DisplayedSweepTimeUtc = sweep.ScanTimeUtc;
        DisplayedSweep = sweep;
        mapView.ShowSweep(sweep, CurrentTable);
        SelectionChanged?.Invoke();
        StatusChanged?.Invoke(
            $"{sweep.SiteId}  {sweep.ScanTimeUtc:HH:mm:ss}Z  " +
            (relative ? "Storm-relative velocity" : _moment.ToString()) + "  " +
            $"{sweep.ElevationAngleDeg:F1}°  (tilt {_cutPosition + 1} of {cuts.Count})" +
            (relative ? $"  [motion {StormMotion.SpeedKmh:F0} km/h from {StormMotion.BearingDeg:F0}°]" : ""));
    }
}
