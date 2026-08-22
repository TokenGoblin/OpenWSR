using System.Collections.Concurrent;
using OpenWSR.Nexrad;
using OpenWSR.Nexrad.Analysis;
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

    /// <summary>
    /// Unfold velocities past the Nyquist limit. Off by default: it rewrites measured
    /// values, and on a quiet day there is nothing to unfold.
    /// </summary>
    public bool DealiasVelocity { get; set; }

    /// <summary>True when the displayed sweep is actually being unfolded — the moment is
    /// velocity, the option is on, and the cut reported a Nyquist velocity to unfold against.</summary>
    public bool IsDealiasing =>
        DealiasVelocity && _moment == Moment.Velocity &&
        CutsForMoment(_moment).ElementAtOrDefault(_cutPosition)?.AliasingIntervalMs is not null;

    /// <summary>
    /// Which products can be shown. Azimuthal shear is not in the volume — nothing in
    /// Message 31 carries it — so it is offered whenever velocity is, because that is what
    /// it is computed from.
    /// </summary>
    public IReadOnlyCollection<Moment> AvailableMoments
    {
        get
        {
            if (_volume is null) return [];
            var moments = _volume.Sweeps.Select(s => s.Moment).Distinct().ToHashSet();
            if (moments.Contains(Moment.Velocity)) moments.Add(Moment.AzimuthalShear);
            return moments;
        }
    }

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
    /// <summary>
    /// An imported table always wins. Otherwise unfolded velocity gets the wider scale,
    /// because the standard one tops out at 35 m/s and would clip everything the unfold
    /// just recovered into one saturated colour.
    /// </summary>
    public ColorTable CurrentTable =>
        _customTables.TryGetValue(_moment, out var custom) ? custom
        : IsDealiasing ? BuiltinTables.DealiasedVelocity
        : BuiltinTables.For(_moment);

    /// <summary>Azimuthal shear is computed here rather than decoded; the status line says so.</summary>
    public bool IsDerived => _moment == Moment.AzimuthalShear;

    /// <summary>Scan time of the sweep currently on screen — feeds the data-age indicator.</summary>
    public DateTime? DisplayedSweepTimeUtc { get; private set; }

    /// <summary>The sweep currently on screen — feeds the hover inspector.</summary>
    public Sweep? DisplayedSweep { get; private set; }

    /// <summary>
    /// Every velocity cut in the volume, whatever product is on screen. The wind profile
    /// needs them all — it works in heights, and each cut reaches a different one.
    /// </summary>
    public IReadOnlyList<Sweep> AllVelocitySweeps() =>
        _volume is null
            ? []
            : [.. _volume.Sweeps.Where(s => s.Moment == Moment.Velocity).OrderBy(s => s.ElevationIndex)];

    /// <summary>Every sweep of the current moment, for analysis across the whole volume.</summary>
    public IReadOnlyList<Sweep> SweepsForCurrentMoment() => [.. CutsForMoment(_moment).Select(Materialise)];

    /// <summary>
    /// The corrections that turn a decoded sweep into the displayed one, in the order they
    /// have to happen: unfold first, because storm-relative subtracts a real motion and
    /// subtracting it from a folded value just moves the discontinuity. Every consumer —
    /// the map, the loop, the cross-section — goes through here so they cannot disagree.
    /// </summary>
    private Sweep Prepare(Sweep sweep)
    {
        if (DealiasVelocity)
            sweep = VelocityDealiasing.Dealias(sweep);
        if (StormRelative && sweep.Moment == Moment.Velocity && StormMotion.SpeedKmh > 0)
            sweep = ToStormRelative(sweep, StormMotion.SpeedKmh, StormMotion.BearingDeg);
        return sweep;
    }

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
        _shearCache.Clear();
        Apply(); // moment and cut position survive volume changes (scrubbing/looping)
    }

    /// <summary>The sweep the current moment/tilt selection picks from an arbitrary volume.</summary>
    public Sweep? SelectSweep(RadarVolume volume)
    {
        var basis = _moment == Moment.AzimuthalShear ? Moment.Velocity : _moment;
        var cuts = volume.Sweeps.Where(s => s.Moment == basis)
            .OrderBy(s => s.ElevationIndex)
            .ToList();
        if (cuts.Count == 0) return null;
        return Materialise(cuts[Math.Clamp(_cutPosition, 0, cuts.Count - 1)]);
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
            case 0x41: SetMoment(Moment.AzimuthalShear); return true;           // A
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

    /// <summary>
    /// The cuts backing a product. For azimuthal shear these are the velocity cuts it is
    /// derived from — same count, same elevations, same order — so the tilt list is right
    /// without computing a single sweep of shear.
    /// </summary>
    private List<Sweep> CutsForMoment(Moment moment) =>
        _volume?.Sweeps
            .Where(s => s.Moment == (moment == Moment.AzimuthalShear ? Moment.Velocity : moment))
            .OrderBy(s => s.ElevationIndex)
            .ToList() ?? [];

    /// <summary>
    /// Derived products are computed here, once per cut, because computing one costs real
    /// time and scrubbing a loop would otherwise pay it on every frame.
    ///
    /// Concurrent because the 3D view materialises a whole volume on the thread pool while
    /// the map is materialising the displayed cut on the UI thread. A plain dictionary
    /// written from both corrupts. Two threads racing to compute the same cut only waste
    /// the work; one of the two results wins and they are equal.
    /// </summary>
    private readonly ConcurrentDictionary<(int Elevation, DateTime Time), Sweep> _shearCache = new();

    /// <summary>
    /// Turn a backing cut into the sweep actually displayed.
    ///
    /// Shear always unfolds first, whatever the velocity toggle says. A fold is a
    /// 2·V<sub>nyquist</sub> step between adjacent radials, and differentiating across it
    /// produces shear several times larger than any real vortex — so on a folded field the
    /// product shows its own artefacts rather than the rotation it exists to reveal.
    /// </summary>
    private Sweep Materialise(Sweep basis)
    {
        if (_moment != Moment.AzimuthalShear) return Prepare(basis);

        var key = (basis.ElevationIndex, basis.ScanTimeUtc);
        if (_shearCache.TryGetValue(key, out var cached)) return cached;

        var shear = AzimuthalShear.Compute(VelocityDealiasing.Dealias(basis));

        // Shear computed where there is no echo is the phase of receiver noise, not
        // rotation. Blanking it here rather than in the renderer keeps the live product and
        // the rotation-track swath showing the same field.
        if (_volume is not null &&
            GateQuality.ReflectivityFor(_volume.Sweeps, basis) is { } reflectivity)
            shear = GateQuality.MaskByReflectivity(shear, reflectivity);

        if (_shearCache.Count > 32) _shearCache.Clear(); // one volume's worth is plenty
        _shearCache[key] = shear;
        return shear;
    }

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
        var raw = cuts[_cutPosition];
        bool relative = StormRelative && _moment == Moment.Velocity && StormMotion.SpeedKmh > 0;
        bool unfolded = DealiasVelocity && raw.AliasingIntervalMs is not null &&
                        _moment == Moment.Velocity;
        var sweep = Materialise(raw);

        DisplayedSweepTimeUtc = sweep.ScanTimeUtc;
        DisplayedSweep = sweep;
        mapView.ShowSweep(sweep, CurrentTable);
        SelectionChanged?.Invoke();

        string product = _moment switch
        {
            Moment.AzimuthalShear => "Azimuthal shear (from unfolded velocity)",
            _ when relative => "Storm-relative velocity",
            _ => _moment.ToString(),
        };
        if (unfolded) product += " (unfolded)";
        // The Nyquist is deliberately not repeated here — the layers panel states it, and
        // this line shares its row with the inspector readout, which gets trimmed away.
        StatusChanged?.Invoke(
            $"{sweep.SiteId}  {sweep.ScanTimeUtc:HH:mm:ss}Z  {product}  " +
            $"{sweep.ElevationAngleDeg:F1}°  (tilt {_cutPosition + 1} of {cuts.Count})" +
            (relative ? $"  [motion {StormMotion.SpeedKmh:F0} km/h from {StormMotion.BearingDeg:F0}°]" : ""));
    }
}
