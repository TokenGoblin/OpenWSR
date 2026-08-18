using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenWSR.Grib2;

public sealed class Grib2FormatException(string message) : Exception(message);

/// <summary>Where a value sits on the earth. Only the grids OpenWSR consumes are modelled.</summary>
public enum GridKind { LatLon, LambertConformal }

/// <summary>Geometry of the value grid, decoded from Section 3.</summary>
public sealed record Grib2Grid(
    GridKind Kind,
    int Nx, int Ny,
    double Lat1Deg, double Lon1Deg,
    double DxDeg, double DyDeg,          // lat/lon grids: degrees
    double DxMetres, double DyMetres,    // projected grids: metres
    double LovDeg, double Latin1Deg, double Latin2Deg, double LadDeg,
    byte ScanningMode)
{
    public int PointCount => Nx * Ny;

    /// <summary>True when rows run north to south (bit 2 of the scanning mode is clear).</summary>
    public bool NorthToSouth => (ScanningMode & 0x40) == 0;
}

/// <summary>One decoded GRIB2 field: its grid, its values, and how to spot missing data.</summary>
public sealed record Grib2Field(
    int Discipline, int Category, int Parameter,
    DateTime ReferenceTimeUtc,
    int ForecastHours,
    Grib2Grid Grid,
    float[] Values,
    float MissingValue);

/// <summary>
/// A focused GRIB2 edition-2 reader: the sections and templates the weather products
/// OpenWSR uses actually employ — lat/lon and Lambert grids, simple, complex with
/// spatial differencing, and PNG packing. Not a general-purpose implementation.
/// </summary>
public static class Grib2File
{
    public static Grib2Field Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length >= 2 && file[0] == 0x1F && file[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(file.ToArray()), CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            gz.CopyTo(buffer);
            return Decode(buffer.ToArray().AsSpan());
        }

        if (file.Length < 16 || !file[..4].SequenceEqual("GRIB"u8))
            throw new Grib2FormatException("Not a GRIB message.");
        if (file[7] != 2)
            throw new Grib2FormatException($"Only GRIB edition 2 is supported, found {file[7]}.");

        int discipline = file[6];
        Grib2Grid? grid = null;
        DateTime referenceTime = default;
        int category = -1, parameter = -1, forecastHours = 0;
        DrsInfo? drs = null;
        bool[]? bitmap = null;

        int pos = 16;
        while (pos + 5 <= file.Length)
        {
            if (file.Slice(pos, Math.Min(4, file.Length - pos)).SequenceEqual("7777"u8))
                break;
            int length = BinaryPrimitives.ReadInt32BigEndian(file[pos..]);
            int number = file[pos + 4];
            if (length <= 0 || pos + length > file.Length)
                throw new Grib2FormatException($"Section {number} has a bad length.");
            var section = file.Slice(pos, length);

            switch (number)
            {
                case 1:
                    referenceTime = new DateTime(
                        BinaryPrimitives.ReadUInt16BigEndian(section[12..]),
                        section[14], section[15], section[16], section[17], section[18],
                        DateTimeKind.Utc);
                    break;
                case 3:
                    grid = ReadGrid(section);
                    break;
                case 4:
                    category = section[9];
                    parameter = section[10];
                    // Octet 19 is the forecast time in the units given by octet 18.
                    if (section.Length >= 22)
                    {
                        int unit = section[17];
                        int amount = BinaryPrimitives.ReadInt32BigEndian(section[18..]);
                        forecastHours = unit switch
                        {
                            0 => amount / 60,   // minutes
                            1 => amount,        // hours
                            _ => amount,
                        };
                    }
                    break;
                case 5:
                    drs = ReadDrs(section);
                    break;
                case 6:
                    bitmap = ReadBitmap(section, grid?.PointCount ?? 0);
                    break;
                case 7:
                    if (grid is null || drs is null)
                        throw new Grib2FormatException("Data section arrived before its grid or packing.");
                    var values = Unpack(section[5..], drs, grid.PointCount, bitmap);
                    return new Grib2Field(
                        discipline, category, parameter, referenceTime, forecastHours,
                        grid, values, MissingValue);
            }
            pos += length;
        }
        throw new Grib2FormatException("No data section found.");
    }

    /// <summary>Sentinel written where the bitmap says a point carries no data.</summary>
    public const float MissingValue = float.NaN;

    // ---- Section 3: grid definition ----

    private static Grib2Grid ReadGrid(ReadOnlySpan<byte> s)
    {
        int template = BinaryPrimitives.ReadUInt16BigEndian(s[12..]);
        return template switch
        {
            0 => ReadLatLonGrid(s),
            30 => ReadLambertGrid(s),
            _ => throw new Grib2FormatException($"Grid template 3.{template} is not supported."),
        };
    }

    private static Grib2Grid ReadLatLonGrid(ReadOnlySpan<byte> s)
    {
        int nx = BinaryPrimitives.ReadInt32BigEndian(s[30..]);
        int ny = BinaryPrimitives.ReadInt32BigEndian(s[34..]);
        double lat1 = BinaryPrimitives.ReadInt32BigEndian(s[46..]) / 1e6;
        double lon1 = BinaryPrimitives.ReadInt32BigEndian(s[50..]) / 1e6;
        double di = BinaryPrimitives.ReadInt32BigEndian(s[63..]) / 1e6;
        double dj = BinaryPrimitives.ReadInt32BigEndian(s[67..]) / 1e6;
        byte scanning = s[71];
        return new Grib2Grid(GridKind.LatLon, nx, ny, lat1, Normalize(lon1), di, dj,
            0, 0, 0, 0, 0, 0, scanning);
    }

    private static Grib2Grid ReadLambertGrid(ReadOnlySpan<byte> s)
    {
        int nx = BinaryPrimitives.ReadInt32BigEndian(s[30..]);
        int ny = BinaryPrimitives.ReadInt32BigEndian(s[34..]);
        double lat1 = BinaryPrimitives.ReadInt32BigEndian(s[38..]) / 1e6;
        double lon1 = BinaryPrimitives.ReadInt32BigEndian(s[42..]) / 1e6;
        double lad = BinaryPrimitives.ReadInt32BigEndian(s[47..]) / 1e6;
        double lov = BinaryPrimitives.ReadInt32BigEndian(s[51..]) / 1e6;
        double dx = BinaryPrimitives.ReadInt32BigEndian(s[55..]) / 1e3;
        double dy = BinaryPrimitives.ReadInt32BigEndian(s[59..]) / 1e3;
        byte scanning = s[64];
        double latin1 = BinaryPrimitives.ReadInt32BigEndian(s[65..]) / 1e6;
        double latin2 = BinaryPrimitives.ReadInt32BigEndian(s[69..]) / 1e6;
        return new Grib2Grid(GridKind.LambertConformal, nx, ny, lat1, Normalize(lon1),
            0, 0, dx, dy, Normalize(lov), latin1, latin2, lad, scanning);
    }

    private static double Normalize(double lonDeg) => lonDeg > 180 ? lonDeg - 360 : lonDeg;

    // ---- Section 5: data representation ----

    private sealed record DrsInfo(
        int Template, float Reference, int BinaryScale, int DecimalScale, int Bits,
        // complex packing (5.2 / 5.3)
        int GroupSplitting, int MissingManagement, int GroupCount,
        int GroupWidthReference, int GroupWidthBits,
        int GroupLengthReference, int GroupLengthIncrement, int LastGroupLength, int GroupLengthBits,
        int SpatialOrder, int ExtraOctets);

    private static DrsInfo ReadDrs(ReadOnlySpan<byte> s)
    {
        int template = BinaryPrimitives.ReadUInt16BigEndian(s[9..]);
        float reference = BinaryPrimitives.ReadSingleBigEndian(s[11..]);
        int binaryScale = Signed16(s, 15);
        int decimalScale = Signed16(s, 17);
        int bits = s[19];

        if (template is 2 or 3)
        {
            return new DrsInfo(template, reference, binaryScale, decimalScale, bits,
                s[21], s[22],
                BinaryPrimitives.ReadInt32BigEndian(s[31..]),
                s[35], s[36],
                BinaryPrimitives.ReadInt32BigEndian(s[37..]), s[41],
                BinaryPrimitives.ReadInt32BigEndian(s[42..]), s[46],
                template == 3 ? s[47] : 0,
                template == 3 ? s[48] : 0);
        }
        return new DrsInfo(template, reference, binaryScale, decimalScale, bits,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static int Signed16(ReadOnlySpan<byte> s, int offset)
    {
        int raw = BinaryPrimitives.ReadUInt16BigEndian(s[offset..]);
        // GRIB uses sign-and-magnitude, not two's complement.
        return (raw & 0x8000) != 0 ? -(raw & 0x7FFF) : raw;
    }

    // ---- Section 6: bitmap ----

    private static bool[]? ReadBitmap(ReadOnlySpan<byte> s, int points)
    {
        if (s[5] != 0) return null; // 255 = no bitmap
        var present = new bool[points];
        var bits = s[6..];
        for (int i = 0; i < points; i++)
            present[i] = (bits[i >> 3] & (0x80 >> (i & 7))) != 0;
        return present;
    }

    // ---- Section 7: unpacking ----

    private static float[] Unpack(ReadOnlySpan<byte> data, DrsInfo drs, int points, bool[]? bitmap)
    {
        int packed = bitmap?.Count(p => p) ?? points;
        var raw = drs.Template switch
        {
            0 => UnpackSimple(data, drs.Bits, packed),
            41 or 40 => UnpackPng(data, packed),
            2 or 3 => UnpackComplex(data, drs, packed),
            _ => throw new Grib2FormatException($"Packing template 5.{drs.Template} is not supported."),
        };

        double scale = Math.Pow(2, drs.BinaryScale) / Math.Pow(10, drs.DecimalScale);
        double offset = drs.Reference / Math.Pow(10, drs.DecimalScale);

        var values = new float[points];
        if (bitmap is null)
        {
            for (int i = 0; i < points; i++)
                values[i] = (float)(offset + raw[i] * scale);
        }
        else
        {
            int source = 0;
            for (int i = 0; i < points; i++)
                values[i] = bitmap[i] ? (float)(offset + raw[source++] * scale) : MissingValue;
        }
        return values;
    }

    private static long[] UnpackSimple(ReadOnlySpan<byte> data, int bits, int count)
    {
        var output = new long[count];
        var reader = new BitReader(data);
        for (int i = 0; i < count; i++) output[i] = reader.Read(bits);
        return output;
    }

    private static long[] UnpackPng(ReadOnlySpan<byte> data, int count)
    {
        var samples = MiniPng.DecodeGrayscale(data, out _, out _);
        var output = new long[count];
        for (int i = 0; i < count && i < samples.Length; i++) output[i] = samples[i];
        return output;
    }

    /// <summary>
    /// Complex packing (5.2), optionally with spatial differencing (5.3): values are
    /// split into groups, each with its own reference and bit width, and the series may
    /// additionally be stored as first or second differences.
    /// </summary>
    private static long[] UnpackComplex(ReadOnlySpan<byte> data, DrsInfo drs, int count)
    {
        var reader = new BitReader(data);
        long firstValue = 0, secondValue = 0, minimumDifference = 0;

        if (drs.SpatialOrder > 0)
        {
            int bits = drs.ExtraOctets * 8;
            firstValue = reader.Read(bits);
            if (drs.SpatialOrder == 2) secondValue = reader.Read(bits);
            minimumDifference = SignAndMagnitude(reader.Read(bits), bits);
        }

        int groups = drs.GroupCount;
        var references = new long[groups];
        for (int g = 0; g < groups; g++) references[g] = reader.Read(drs.Bits);

        reader.AlignToByte();
        var widths = new int[groups];
        for (int g = 0; g < groups; g++)
            widths[g] = drs.GroupWidthReference + (int)reader.Read(drs.GroupWidthBits);

        reader.AlignToByte();
        var lengths = new int[groups];
        for (int g = 0; g < groups; g++)
            lengths[g] = drs.GroupLengthReference + (int)reader.Read(drs.GroupLengthBits) * drs.GroupLengthIncrement;
        lengths[groups - 1] = drs.LastGroupLength;

        reader.AlignToByte();
        var output = new long[count];
        int index = 0;
        for (int g = 0; g < groups && index < count; g++)
        {
            for (int i = 0; i < lengths[g] && index < count; i++)
                output[index++] = references[g] + (widths[g] > 0 ? reader.Read(widths[g]) : 0);
        }

        if (drs.SpatialOrder > 0)
        {
            // Undo the differencing, then remove the minimum that was subtracted.
            if (drs.SpatialOrder == 1)
            {
                output[0] = firstValue;
                for (int i = 1; i < count; i++)
                    output[i] += minimumDifference + output[i - 1];
            }
            else
            {
                output[0] = firstValue;
                if (count > 1) output[1] = secondValue;
                for (int i = 2; i < count; i++)
                    output[i] += minimumDifference + 2 * output[i - 1] - output[i - 2];
            }
        }
        return output;
    }

    private static long SignAndMagnitude(long raw, int bits)
    {
        long signBit = 1L << (bits - 1);
        return (raw & signBit) != 0 ? -(raw & (signBit - 1)) : raw;
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bitPosition;

        public long Read(int bits)
        {
            long value = 0;
            for (int i = 0; i < bits; i++)
            {
                int byteIndex = _bitPosition >> 3;
                int bit = byteIndex < _data.Length
                    ? (_data[byteIndex] >> (7 - (_bitPosition & 7))) & 1
                    : 0;
                value = (value << 1) | (uint)bit;
                _bitPosition++;
            }
            return value;
        }

        public void AlignToByte() => _bitPosition = (_bitPosition + 7) & ~7;
    }
}
