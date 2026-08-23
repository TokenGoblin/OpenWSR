using OpenWSR.Nexrad.Internal;

namespace OpenWSR.Nexrad.Level3;

/// <summary>
/// A digital radial-image Level III product (packet 16), e.g. DVL digital VIL
/// (product 134): one byte level per gate on 1° radials.
/// </summary>
public sealed record RadialImageProduct(
    int ProductCode,
    string SiteId,
    double RadarLatDeg, double RadarLonDeg,
    DateTime VolumeTimeUtc, DateTime ProductTimeUtc,
    float ElevationAngleDeg,
    int FirstBinIndex,
    int GateCount,
    int RadialCount,
    float[] StartAnglesDeg,   // per radial
    float[] DeltaAnglesDeg,   // per radial
    byte[] Levels,            // radialCount * gateCount
    ushort[] Thresholds)      // PDB halfwords 31–46, product-specific encoding
{
    public byte LevelAt(int radial, int gate) => Levels[radial * GateCount + gate];
}

public static class RadialImage
{
    /// <summary>Decode a packet-16 product. Throws when the symbology is not packet 16.</summary>
    public static RadialImageProduct Decode(ReadOnlySpan<byte> file)
    {
        var (msg, siteId) = Level3File.OpenMessage(file);
        int productCode = Be.I16(msg, 0);
        double lat = Be.I32(msg, 20) / 1000.0;
        double lon = Be.I32(msg, 24) / 1000.0;
        var volumeTime = NexradTime.FromJulian(Be.I16(msg, 40), Be.U32(msg, 42) * 1000);
        var productTime = NexradTime.FromJulian(Be.I16(msg, 46), Be.U32(msg, 48) * 1000);

        // Halfword 30, immediately before the thresholds at 31. Tenths of a degree, and
        // signed because the lowest cuts of some products are reported below the horizon.
        float elevationDeg = Be.I16(msg, 58) * 0.1f;

        var thresholds = new ushort[16];
        for (int i = 0; i < 16; i++)
            thresholds[i] = Be.U16(msg, 60 + i * 2);

        int symbologyOffset = checked((int)(Be.U32(msg, 108) * 2));
        if (symbologyOffset <= 0 || symbologyOffset >= msg.Length)
            throw new NexradFormatException("Radial image product has no symbology block.");
        var s = Level3File.DecompressBlock(msg[symbologyOffset..]);
        var span = s.AsSpan();
        if (Be.I16(span, 0) != -1 || Be.I16(span, 2) != 1)
            throw new NexradFormatException("Symbology block header malformed.");

        // First packet of the first layer must be code 16.
        int pos = 10 + 6;
        int code = Be.I16(span, pos);
        if (code != 16)
            throw new NexradFormatException($"Expected digital radial packet 16, found {code}.");
        int firstBin = Be.I16(span, pos + 2);
        int gates = Be.U16(span, pos + 4);
        int radials = Be.U16(span, pos + 12);
        pos += 14;

        var startAngles = new float[radials];
        var deltaAngles = new float[radials];
        var levels = new byte[radials * gates];
        for (int r = 0; r < radials; r++)
        {
            int byteCount = Be.U16(span, pos);
            startAngles[r] = Be.U16(span, pos + 2) * 0.1f;
            deltaAngles[r] = Be.U16(span, pos + 4) * 0.1f;
            pos += 6;
            int copy = Math.Min(byteCount, gates);
            span.Slice(pos, copy).CopyTo(levels.AsSpan(r * gates, copy));
            pos += byteCount;
        }

        return new RadialImageProduct(
            productCode, siteId, lat, lon, volumeTime, productTime, elevationDeg,
            firstBin, gates, radials, startAngles, deltaAngles, levels, thresholds);
    }
}

/// <summary>Digital VIL (product 134) level decoding, per ICD 2620001 and the
/// packed float16 used by its threshold halfwords.</summary>
public static class DigitalVil
{
    public const int ProductCode = 134;

    /// <summary>Gate spacing of the DVL grid, meters.</summary>
    public const double GateSpacingM = 1000.0;

    /// <summary>VIL in kg/m² for a level byte; null for below-threshold/flagged levels.</summary>
    public static double? Value(byte level, IReadOnlyList<ushort> thresholds)
    {
        if (level < 2) return null; // 0 = below threshold, 1 = flagged
        double scale = Float16(thresholds[0]);
        double offset = Float16(thresholds[1]);
        int logStart = thresholds[2];
        double logScale = Float16(thresholds[3]);
        double logOffset = Float16(thresholds[4]);
        return level < logStart
            ? (level - offset) / scale
            : Math.Exp((level - logOffset) / logScale);
    }

    /// <summary>ICD packed float16: sign(1) exponent(5) fraction(10), bias 16.</summary>
    internal static double Float16(ushort halfword)
    {
        int sign = (halfword >> 15) & 1;
        int exponent = (halfword >> 10) & 0x1F;
        int fraction = halfword & 0x3FF;
        double magnitude = exponent > 0
            ? Math.Pow(2, exponent - 16) * (1 + fraction / 1024.0)
            : Math.Pow(2, -15) * (fraction / 1024.0);
        return sign == 1 ? -magnitude : magnitude;
    }
}
