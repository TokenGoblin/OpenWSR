using OpenWSR.Nexrad.Internal;

namespace OpenWSR.Nexrad;

/// <summary>One decoded moment from a Message 31 radial.</summary>
public sealed record RadialMoment(
    Moment Moment,
    int GateCount,
    float FirstGateM,
    float GateSpacingM,
    ScaleInfo Scale,
    float[] Values,          // physical units; NaN = below threshold or range folded
    bool[] RangeFolded);     // parallel to Values

/// <summary>A decoded Message 31 (Digital Radar Data Generic Format) radial.</summary>
public sealed record RadialMessage(
    string Icao,
    DateTime TimeUtc,
    int AzimuthNumber,
    float AzimuthDeg,
    RadialStatus Status,
    int ElevationNumber,
    float ElevationDeg,
    VolumeInfo? Volume,      // present when the RVOL block is in this radial
    float? NyquistMs,        // from the RRAD block
    IReadOnlyList<RadialMoment> Moments);

/// <summary>Contents of the Message 31 RVOL data block.</summary>
public sealed record VolumeInfo(
    double LatDeg, double LonDeg, double SiteHeightM, double FeedhornHeightM, int VcpNumber)
{
    /// <summary>Beam origin height: ground elevation plus feedhorn.</summary>
    public double RadarAltitudeM => SiteHeightM + FeedhornHeightM;
}

public enum RadialStatus : byte
{
    StartOfElevation = 0,
    Intermediate = 1,
    EndOfElevation = 2,
    StartOfVolume = 3,
    EndOfVolume = 4,
    StartNewElevation = 5,
}

/// <summary>Minimal Message 5 (volume coverage pattern definition).</summary>
public sealed record VcpDefinition(int PatternNumber, IReadOnlyList<VcpCut> Cuts);

public sealed record VcpCut(float ElevationDeg, int WaveformType, bool SuperResolution);

/// <summary>Minimal Message 2 (RDA status).</summary>
public sealed record RdaStatus(int Status, int OperabilityStatus, int VcpNumber);

/// <summary>
/// Walks the messages inside one decompressed LDM record. Each message is a 12-byte CTM
/// header followed by a 16-byte message header. Message 31 is variable-length; every
/// legacy message type occupies a fixed 2432-byte frame.
/// </summary>
public static class MessageReader
{
    private const int CtmSize = 12;
    private const int HeaderSize = 16;
    private const int LegacyFrameSize = 2432; // includes the CTM prefix

    public static void ReadMessages(
        ReadOnlySpan<byte> record,
        Action<RadialMessage> onRadial,
        Action<VcpDefinition>? onVcp = null,
        Action<RdaStatus>? onStatus = null)
    {
        int pos = 0;
        while (pos + CtmSize + HeaderSize <= record.Length)
        {
            var header = record.Slice(pos + CtmSize, HeaderSize);
            int sizeHalfwords = Be.U16(header, 0);
            byte type = header[3];

            if (type == 31)
            {
                // Size > 65534 halfwords cannot fit the 16-bit field; the actual size is
                // then carried as a 32-bit value in the segment count/number fields.
                int sizeBytes = sizeHalfwords == 0xFFFF
                    ? checked((int)(((uint)Be.U16(header, 12) << 16 | Be.U16(header, 14)) * 2))
                    : sizeHalfwords * 2;
                if (sizeBytes < HeaderSize || pos + CtmSize + sizeBytes > record.Length)
                    throw new NexradFormatException($"Message 31 size {sizeBytes} overruns record.");

                var body = record.Slice(pos + CtmSize + HeaderSize, sizeBytes - HeaderSize);
                int julian = Be.U16(header, 6);
                uint millis = Be.U32(header, 8);
                onRadial(ParseMessage31(body, NexradTime.FromJulian(julian, millis)));
                pos += CtmSize + sizeBytes;
            }
            else
            {
                if (sizeHalfwords > 0 && pos + LegacyFrameSize <= record.Length)
                {
                    var body = record.Slice(pos + CtmSize + HeaderSize, LegacyFrameSize - CtmSize - HeaderSize);
                    if (type == 5 && onVcp is not null) onVcp(ParseMessage5(body));
                    if (type == 2 && onStatus is not null) onStatus(ParseMessage2(body));
                }
                pos += LegacyFrameSize;
            }
        }
    }

    private static RadialMessage ParseMessage31(ReadOnlySpan<byte> body, DateTime headerTime)
    {
        string icao = Be.Ascii(body, 0, 4);
        uint collectMs = Be.U32(body, 4);
        int collectDate = Be.U16(body, 8);
        int azimuthNumber = Be.U16(body, 10);
        float azimuth = Be.F32(body, 12);
        byte compression = body[16];
        if (compression != 0)
            throw new NexradFormatException($"Radial-level compression {compression} is not supported.");
        var status = (RadialStatus)body[21];
        int elevationNumber = body[22];
        float elevation = Be.F32(body, 24);
        int blockCount = Be.U16(body, 30);

        VolumeInfo? volume = null;
        float? nyquist = null;
        var moments = new List<RadialMoment>(6);

        for (int i = 0; i < Math.Min(blockCount, 10); i++)
        {
            int pointerOffset = 32 + i * 4;
            if (pointerOffset + 4 > body.Length) break;
            int p = checked((int)Be.U32(body, pointerOffset));
            if (p <= 0 || p + 4 > body.Length) continue;

            string tag = Be.Ascii(body, p, 4); // e.g. "RVOL", "DREF", "DSW "
            switch (tag)
            {
                case "RVOL":
                    volume = new VolumeInfo(
                        Be.F32(body, p + 8), Be.F32(body, p + 12),
                        Be.I16(body, p + 16), Be.U16(body, p + 18),
                        Be.U16(body, p + 40));
                    break;
                case "RRAD":
                    nyquist = Be.U16(body, p + 16) * 0.01f;
                    break;
                case "RELV":
                    break;
                default:
                    if (tag[0] == 'D' && TryMapMoment(tag, out var moment))
                        moments.Add(ParseMomentBlock(body, p, moment));
                    // Unknown block names (future builds) are skipped by design.
                    break;
            }
        }

        return new RadialMessage(
            icao, NexradTime.FromJulian(collectDate, collectMs),
            azimuthNumber, azimuth, status, elevationNumber, elevation,
            volume, nyquist, moments);
    }

    private static bool TryMapMoment(string tag, out Moment moment)
    {
        switch (tag.TrimEnd())
        {
            case "DREF": moment = Moment.Reflectivity; return true;
            case "DVEL": moment = Moment.Velocity; return true;
            case "DSW": moment = Moment.SpectrumWidth; return true;
            case "DZDR": moment = Moment.DifferentialReflectivity; return true;
            case "DPHI": moment = Moment.DifferentialPhase; return true;
            case "DRHO": moment = Moment.CorrelationCoefficient; return true;
            case "DCFP": moment = Moment.ClutterFilterPower; return true;
            default: moment = default; return false;
        }
    }

    private static RadialMoment ParseMomentBlock(ReadOnlySpan<byte> body, int p, Moment moment)
    {
        int gates = Be.U16(body, p + 8);
        float firstGateM = Be.I16(body, p + 10);   // stored in units of 0.001 km = meters
        float gateSpacingM = Be.I16(body, p + 12); // same units
        int wordSize = body[p + 19];
        float scale = Be.F32(body, p + 20);
        float offset = Be.F32(body, p + 24);
        int dataStart = p + 28;

        var values = new float[gates];
        var folded = new bool[gates];
        for (int g = 0; g < gates; g++)
        {
            uint raw = wordSize switch
            {
                8 => body[dataStart + g],
                16 => Be.U16(body, dataStart + g * 2),
                _ => throw new NexradFormatException($"Unsupported moment word size {wordSize}."),
            };
            if (raw == 0) { values[g] = float.NaN; }                       // below threshold
            else if (raw == 1) { values[g] = float.NaN; folded[g] = true; } // range folded
            else values[g] = scale == 0 ? raw : (raw - offset) / scale;
        }

        return new RadialMoment(moment, gates, firstGateM, gateSpacingM,
            new ScaleInfo(scale, offset, wordSize), values, folded);
    }

    private static VcpDefinition ParseMessage5(ReadOnlySpan<byte> body)
    {
        int patternNumber = Be.U16(body, 4);
        int cutCount = Be.U16(body, 6);
        var cuts = new List<VcpCut>(cutCount);
        const int fixedSize = 22, cutSize = 46;
        for (int i = 0; i < cutCount; i++)
        {
            int o = fixedSize + i * cutSize;
            if (o + cutSize > body.Length) break;
            // Elevation is a 16-bit binary angle: 180 deg / 2^15 per count.
            float elevation = Be.U16(body, o) * (180f / 32768f);
            int waveform = body[o + 3];
            bool superRes = (body[o + 4] & 0x01) != 0;
            cuts.Add(new VcpCut(elevation, waveform, superRes));
        }
        return new VcpDefinition(patternNumber, cuts);
    }

    private static RdaStatus ParseMessage2(ReadOnlySpan<byte> body) =>
        new(Be.U16(body, 0), Be.U16(body, 2), Be.U16(body, 14));
}
