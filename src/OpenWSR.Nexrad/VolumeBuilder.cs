using System.Collections;

namespace OpenWSR.Nexrad;

/// <summary>
/// Accumulates decoded messages and assembles them into a <see cref="RadarVolume"/>.
/// Pure and incremental: the real-time chunk assembler feeds it record by record, the
/// archive path feeds it a whole file. Radials are grouped by elevation number, so
/// SAILS/MRLE supplemental cuts (duplicate elevation angles, distinct cut numbers)
/// come out as separate sweeps in scan order.
/// </summary>
public sealed class VolumeBuilder
{
    private readonly SortedDictionary<int, List<RadialMessage>> _cuts = [];
    private VcpDefinition? _vcp;
    private RdaStatus? _status;
    private VolumeInfo? _volumeInfo;
    private string? _icao;

    public void AddRecord(ReadOnlySpan<byte> decompressedRecord) =>
        MessageReader.ReadMessages(decompressedRecord, AddRadial, v => _vcp = v, s => _status = s);

    public void AddRadial(RadialMessage radial)
    {
        _icao ??= radial.Icao;
        _volumeInfo ??= radial.Volume;
        if (!_cuts.TryGetValue(radial.ElevationNumber, out var list))
            _cuts[radial.ElevationNumber] = list = new List<RadialMessage>(800);
        list.Add(radial);
    }

    public bool HasData => _cuts.Count > 0;

    /// <summary>Elevation numbers that have received their end-of-elevation radial.</summary>
    public IEnumerable<int> CompletedCuts =>
        _cuts.Where(kv => kv.Value.Any(r =>
                r.Status is RadialStatus.EndOfElevation or RadialStatus.EndOfVolume))
            .Select(kv => kv.Key);

    public RadarVolume Build()
    {
        if (_cuts.Count == 0)
            throw new InvalidOperationException("No radials to build a volume from.");
        var info = _volumeInfo
            ?? throw new NexradFormatException("No RVOL block seen — cannot georeference volume.");

        var sweeps = new List<Sweep>();
        foreach (var (elevationNumber, radials) in _cuts)
        {
            radials.Sort((a, b) => a.AzimuthNumber.CompareTo(b.AzimuthNumber));
            foreach (var moment in radials.SelectMany(r => r.Moments).Select(m => m.Moment).Distinct())
                sweeps.Add(BuildSweep(elevationNumber, radials, moment, info));
        }

        var start = sweeps.Count > 0 ? sweeps.Min(s => s.ScanTimeUtc) : default;
        return new RadarVolume(
            _icao ?? "????", start,
            info.LatDeg, info.LonDeg, info.RadarAltitudeM,
            info.VcpNumber, _vcp, _status, sweeps);
    }

    private Sweep BuildSweep(int elevationNumber, List<RadialMessage> radials, Moment moment, VolumeInfo info)
    {
        var withMoment = radials.Where(r => r.Moments.Any(m => m.Moment == moment)).ToList();
        var first = withMoment[0].Moments.First(m => m.Moment == moment);

        int gateCount = 0;
        foreach (var r in withMoment)
        {
            var m = r.Moments.First(x => x.Moment == moment);
            if (m.FirstGateM != first.FirstGateM || m.GateSpacingM != first.GateSpacingM)
                throw new NexradFormatException(
                    $"Inconsistent gate geometry within elevation {elevationNumber} moment {moment}.");
            gateCount = Math.Max(gateCount, m.GateCount);
        }

        int radialCount = withMoment.Count;
        var azimuths = new float[radialCount];
        var data = new float[radialCount * gateCount];
        var folded = new BitArray(radialCount * gateCount);
        Array.Fill(data, float.NaN);

        for (int i = 0; i < radialCount; i++)
        {
            var radial = withMoment[i];
            azimuths[i] = radial.AzimuthDeg;
            var m = radial.Moments.First(x => x.Moment == moment);
            Array.Copy(m.Values, 0, data, i * gateCount, m.GateCount);
            for (int g = 0; g < m.GateCount; g++)
                if (m.RangeFolded[g])
                    folded[i * gateCount + g] = true;
        }

        // The RRAD block rides on individual radials and can be absent from some of them;
        // take the first one that carried it. Nyquist is a property of the cut's PRF, so
        // it is the same across the cut when present at all.
        float? nyquist = withMoment.Select(r => r.NyquistMs).FirstOrDefault(n => n is > 0);

        return new Sweep(
            _icao ?? "????",
            withMoment[0].TimeUtc,
            info.LatDeg, info.LonDeg, info.RadarAltitudeM,
            elevationNumber, withMoment[0].ElevationDeg,
            moment, azimuths,
            first.FirstGateM, first.GateSpacingM,
            gateCount, data, first.Scale, folded, nyquist);
    }
}
