using System.Collections;

namespace OpenWSR.Nexrad.Level3;

/// <summary>
/// Terminal Doppler Weather Radar, as reached through Level III.
///
/// The 45-odd TDWRs sit beside major airports and see the ground under them far better than
/// the WSR-88D network does: C-band, a 0.55° beam against the 88D's 0.95°, 150 m gates, and a
/// siting chosen for the approach paths rather than for regional coverage. Over a metro area
/// that is a different picture, not a slightly sharper one.
///
/// They are reached differently, though. TDWR Level II is not published to the NEXRAD bucket,
/// so what is available is the Level III digital radial products — TZ0/TZ1/TZ2 for
/// reflectivity and TV0/TV1/TV2 for velocity, three tilts each. Those live in the same
/// <c>unidata-nexrad-level3</c> bucket, under the same key format, as every other Level III
/// product; the site prefix is the ICAO minus its leading T.
/// </summary>
public static class TdwrRadar
{
    /// <summary>Digital base reflectivity, tilts 0 to 2.</summary>
    public static readonly string[] ReflectivityProducts = ["TZ0", "TZ1", "TZ2"];

    /// <summary>Digital base velocity, tilts 0 to 2.</summary>
    public static readonly string[] VelocityProducts = ["TV0", "TV1", "TV2"];

    /// <summary>
    /// Range gate spacing, metres.
    ///
    /// The ICD value. MetPy reports the nominal range of these products as 90 km, which over
    /// the 592 gates a live file carries would imply 152 m — that is the nominal figure
    /// rounded, not a measurement. The difference is 1.2 km at the far edge of a 90 km
    /// product, well inside one gate of the WSR-88D it will be compared against.
    /// </summary>
    public const double GateSpacingM = 150.0;

    /// <summary>Whether a Level III product code is one of the TDWR digital radial products.</summary>
    public static bool IsTdwrProduct(int productCode) => productCode is >= 180 and <= 187;

    /// <summary>
    /// Physical value for one level byte, or NaN where the gate carries no measurement.
    ///
    /// The scaling is in the product's own threshold halfwords rather than fixed per product:
    /// halfword 31 is the value of the first data level in tenths, 32 the increment in tenths,
    /// 33 the number of levels. Levels 0 and 1 are "below threshold" and "range folded", so
    /// the data starts at 2 — which is why the offset is applied against <c>level - 2</c> and
    /// not against the raw byte.
    /// </summary>
    public static float Value(byte level, IReadOnlyList<ushort> thresholds)
    {
        if (level < 2) return float.NaN;

        double minimum = (short)thresholds[0] / 10.0;
        double increment = thresholds[1] / 10.0;
        if (increment <= 0) return float.NaN;

        // Clamped to what a byte can actually address. DHR advertises 256 levels because it
        // counts the two flag values among them, and taking that at face value would map
        // levels past the end of the table.
        int levels = Math.Min((int)thresholds[2], 254);
        if (level >= levels + 2) return float.NaN;

        return (float)(minimum + (level - 2) * increment);
    }

    /// <summary>
    /// Turn a decoded TDWR product into a <see cref="Sweep"/>.
    ///
    /// Worth doing rather than rendering it as its own thing: a sweep is what the renderer,
    /// the inspector, the palettes, the cross-section and the colour scale all already speak.
    /// A Level III radial image is the same geometry expressed differently — evenly spaced
    /// radials rather than the uneven ones a Level II cut carries, which the renderer handles
    /// either way because it reads the azimuth of each radial rather than assuming one.
    /// </summary>
    /// <param name="altitudeM">
    /// Site elevation, which the Level III header does not carry. Beam heights are wrong
    /// without it, and for a radar sited at an airport in a valley that is not a small error.
    /// </param>
    public static Sweep ToSweep(RadialImageProduct product, Moment moment, double altitudeM)
    {
        int radials = product.RadialCount;
        int gates = product.GateCount;

        // Level 1 means range folded on the velocity products and is simply unused on
        // reflectivity, which is the distinction MetPy draws with its range_fold flag.
        bool foldable = moment is Moment.Velocity or Moment.SpectrumWidth;

        var values = new float[radials * gates];
        var rangeFolded = new BitArray(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            byte level = product.Levels[i];
            values[i] = Value(level, product.Thresholds);
            if (foldable && level == 1) rangeFolded[i] = true;
        }

        // ScaleInfo runs the other way round: value = (raw - Offset) / Scale. Inverting
        // value = minimum + (level - 2) x increment gives Scale = 1/increment and
        // Offset = 2 - minimum/increment. For the usual reflectivity encoding — first level
        // -32 dBZ, half a decibel a step — that is the familiar (level - 66) / 2.
        double increment = product.Thresholds[1] / 10.0;
        double minimum = (short)product.Thresholds[0] / 10.0;
        var scale = increment > 0
            ? new ScaleInfo((float)(1.0 / increment), (float)(2 - minimum / increment), 8)
            : new ScaleInfo(1f, 0f, 8);

        return new Sweep(
            SiteId: product.SiteId,
            ScanTimeUtc: product.VolumeTimeUtc,
            RadarLatDeg: product.RadarLatDeg,
            RadarLonDeg: product.RadarLonDeg,
            RadarAltM: altitudeM,
            ElevationIndex: 0,
            ElevationAngleDeg: product.ElevationAngleDeg,
            Moment: moment,
            AzimuthsDeg: product.StartAnglesDeg,
            // The first gate starts at the radar, not half a gate out: packet 16 reports the
            // index of the first bin and these products start at zero.
            FirstGateM: (float)(product.FirstBinIndex * GateSpacingM),
            GateSpacingM: (float)GateSpacingM,
            GateCount: gates,
            Data: values,
            Scale: scale,
            RangeFoldedMask: rangeFolded);
    }
}
