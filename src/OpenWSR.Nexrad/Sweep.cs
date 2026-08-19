using System.Collections;

namespace OpenWSR.Nexrad;

/// <summary>Per-moment linear scaling from the Message 31 data block header.
/// Physical value = (raw − Offset) / Scale; Scale == 0 means raw values are unscaled.</summary>
public sealed record ScaleInfo(float Scale, float Offset, int WordSizeBits);

/// <summary>
/// One elevation cut of one moment, decoded to physical units.
/// Immutable by design — the arrays are not copied on access; treat them as frozen.
/// </summary>
public sealed record Sweep(
    string SiteId,
    DateTime ScanTimeUtc,
    double RadarLatDeg, double RadarLonDeg, double RadarAltM,
    int ElevationIndex, float ElevationAngleDeg,
    Moment Moment,
    float[] AzimuthsDeg,        // length = radialCount, NOT evenly spaced
    float FirstGateM, float GateSpacingM,
    int GateCount,
    float[] Data,               // radialCount * gateCount, NaN = below threshold / range folded
    ScaleInfo Scale,
    BitArray RangeFoldedMask,   // parallel to Data; true where the gate was range folded
    float? NyquistMs = null)    // RRAD block; the velocity fold limit for this cut
{
    public int RadialCount => AzimuthsDeg.Length;

    /// <summary>
    /// The width of one aliasing interval, 2·V<sub>nyquist</sub>. A true velocity outside
    /// ±V<sub>nyquist</sub> is reported folded by a whole number of these, which is why a
    /// strong couplet can read with the sign reversed until it is unfolded.
    /// </summary>
    public float? AliasingIntervalMs => NyquistMs is { } v and > 0 ? 2f * v : null;
}

/// <summary>A fully decoded volume scan: site metadata plus all sweeps in scan order.</summary>
public sealed record RadarVolume(
    string SiteId,
    DateTime StartTimeUtc,
    double LatDeg, double LonDeg, double AltitudeM,
    int VcpNumber,
    VcpDefinition? Vcp,
    RdaStatus? Status,
    IReadOnlyList<Sweep> Sweeps);
