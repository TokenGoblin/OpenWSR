using System.Buffers.Binary;
using System.Text;

namespace OpenWSR.Geo;

/// <summary>What a shapefile record draws as.</summary>
public enum ShapeKind
{
    Null,
    Point,
    PolyLine,
    Polygon,
    MultiPoint,
}

/// <summary>
/// One shapefile record: its geometry, split into parts, plus the attribute row that sat
/// beside it in the .dbf.
/// </summary>
/// <remarks>
/// A part is a ring for a polygon and a line string for a polyline. A polygon with holes or
/// a state with offshore islands is one record with many parts, which is why this is a list
/// of lists rather than a list of points — Alaska is a single record of 47 parts.
/// </remarks>
public sealed record ShapeFeature(
    ShapeKind Kind,
    IReadOnlyList<IReadOnlyList<(double LatDeg, double LonDeg)>> Parts,
    IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>An attribute by name, or null. Names are case-insensitive.</summary>
    public string? this[string field] =>
        Attributes.TryGetValue(field, out var value) ? value : null;
}

/// <summary>
/// Reads ESRI shapefiles — the format every public boundary dataset ships in.
/// </summary>
/// <remarks>
/// The geometry lives in the .shp and the attributes in a .dbf beside it, matched by
/// position: record <c>n</c> of one belongs to record <c>n</c> of the other, with no key
/// linking them. That is the whole relational model, and it is why both are read together.
///
/// <para>Byte order is mixed <em>within the same header</em>: the file code and record
/// headers are big-endian and everything else is little-endian, a holdover from the format
/// being designed to be read on two architectures at once. Getting this backwards produces
/// plausible-looking garbage rather than an error.</para>
///
/// <para>No reprojection is done. Census cartographic boundary files are NAD83 geographic —
/// degrees already, and within a metre of WGS84, which is far inside the resolution of any
/// boundary line. A shapefile in a projected CRS would need its .prj honoured, and this
/// reader does not do that; it reads x as longitude and y as latitude.</para>
/// </remarks>
public static class Shapefile
{
    private const int FileCode = 9994;
    private const int HeaderBytes = 100;

    /// <summary>
    /// Read geometry, and attributes too when a .dbf is supplied. Records come back in file
    /// order, which is the order the two files agree on.
    /// </summary>
    public static IReadOnlyList<ShapeFeature> Read(byte[] shp, byte[]? dbf = null)
    {
        ArgumentNullException.ThrowIfNull(shp);
        if (shp.Length < HeaderBytes)
            throw new InvalidDataException("Shapefile is shorter than its 100-byte header.");

        int code = BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(0));
        if (code != FileCode)
            throw new InvalidDataException(
                $"Not a shapefile: file code {code}, expected {FileCode}.");

        // Length is in 16-bit words, and counts the header.
        long lengthBytes = BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(24)) * 2L;
        int end = (int)Math.Min(lengthBytes, shp.Length);

        var attributes = dbf is null ? null : ReadDbf(dbf);
        var features = new List<ShapeFeature>();
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int offset = HeaderBytes;
        while (offset + 8 <= end)
        {
            // Record header: number then content length, both big-endian, length in words.
            int contentBytes = BinaryPrimitives.ReadInt32BigEndian(shp.AsSpan(offset + 4)) * 2;
            int content = offset + 8;
            if (contentBytes < 4 || content + contentBytes > end) break;

            var row = attributes is not null && features.Count < attributes.Count
                ? attributes[features.Count]
                : empty;
            features.Add(ReadRecord(shp.AsSpan(content, contentBytes), row));
            offset = content + contentBytes;
        }

        return features;
    }

    private static ShapeFeature ReadRecord(
        ReadOnlySpan<byte> content, IReadOnlyDictionary<string, string> attributes)
    {
        int type = BinaryPrimitives.ReadInt32LittleEndian(content);

        // The Z and M variants (11/13/15/18, 21/23/25/28) repeat their 2-D counterpart's
        // layout and then append the extra ordinates, so reading the head of one as the
        // other is correct and the tail is simply ignored.
        var kind = (type % 10) switch
        {
            0 => ShapeKind.Null,
            1 => ShapeKind.Point,
            3 => ShapeKind.PolyLine,
            5 => ShapeKind.Polygon,
            8 => ShapeKind.MultiPoint,
            _ => ShapeKind.Null,
        };

        switch (kind)
        {
            case ShapeKind.Null:
                return new ShapeFeature(ShapeKind.Null, [], attributes);

            case ShapeKind.Point:
            {
                if (content.Length < 20) return new ShapeFeature(kind, [], attributes);
                double lon = BinaryPrimitives.ReadDoubleLittleEndian(content[4..]);
                double lat = BinaryPrimitives.ReadDoubleLittleEndian(content[12..]);
                return new ShapeFeature(kind, [new[] { (lat, lon) }], attributes);
            }

            case ShapeKind.MultiPoint:
            {
                if (content.Length < 40) return new ShapeFeature(kind, [], attributes);
                int count = BinaryPrimitives.ReadInt32LittleEndian(content[36..]);
                var points = ReadPoints(content, 40, count);
                return new ShapeFeature(kind, [points], attributes);
            }

            default:
            {
                // Polygon and PolyLine share a layout: box, part count, point count, the
                // index where each part starts, then every point of every part end to end.
                if (content.Length < 44) return new ShapeFeature(kind, [], attributes);
                int partCount = BinaryPrimitives.ReadInt32LittleEndian(content[36..]);
                int pointCount = BinaryPrimitives.ReadInt32LittleEndian(content[40..]);
                if (partCount <= 0 || pointCount <= 0)
                    return new ShapeFeature(kind, [], attributes);

                int partsAt = 44;
                int pointsAt = partsAt + partCount * 4;
                if (pointsAt + pointCount * 16 > content.Length)
                    return new ShapeFeature(kind, [], attributes);

                var starts = new int[partCount];
                for (int i = 0; i < partCount; i++)
                    starts[i] = BinaryPrimitives.ReadInt32LittleEndian(content[(partsAt + i * 4)..]);

                var parts = new List<IReadOnlyList<(double, double)>>(partCount);
                for (int i = 0; i < partCount; i++)
                {
                    int from = starts[i];
                    int to = i + 1 < partCount ? starts[i + 1] : pointCount;
                    if (from < 0 || to > pointCount || to <= from) continue;
                    parts.Add(ReadPoints(content, pointsAt + from * 16, to - from));
                }

                return new ShapeFeature(kind, parts, attributes);
            }
        }
    }

    private static (double LatDeg, double LonDeg)[] ReadPoints(
        ReadOnlySpan<byte> content, int offset, int count)
    {
        var points = new (double, double)[count];
        for (int i = 0; i < count; i++)
        {
            int at = offset + i * 16;
            double lon = BinaryPrimitives.ReadDoubleLittleEndian(content[at..]);
            double lat = BinaryPrimitives.ReadDoubleLittleEndian(content[(at + 8)..]);
            points[i] = (lat, lon);
        }
        return points;
    }

    /// <summary>
    /// The dBASE III table beside a shapefile, as one string dictionary per record.
    /// </summary>
    /// <remarks>
    /// Everything comes back as a trimmed string, including numbers. A .dbf stores numerics
    /// as right-aligned ASCII anyway, so parsing them here would only guess at a type the
    /// caller knows better — and a FIPS code such as "06" is a string that would be ruined
    /// by being read as a number.
    /// </remarks>
    private static List<Dictionary<string, string>> ReadDbf(byte[] dbf)
    {
        var rows = new List<Dictionary<string, string>>();
        if (dbf.Length < 32) return rows;

        int recordCount = BinaryPrimitives.ReadInt32LittleEndian(dbf.AsSpan(4));
        int headerBytes = BinaryPrimitives.ReadUInt16LittleEndian(dbf.AsSpan(8));
        int recordBytes = BinaryPrimitives.ReadUInt16LittleEndian(dbf.AsSpan(10));
        if (headerBytes <= 32 || recordBytes <= 0) return rows;

        var names = new List<string>();
        var widths = new List<int>();
        for (int at = 32; at + 32 <= dbf.Length && dbf[at] != 0x0D; at += 32)
        {
            int nameLength = 0;
            while (nameLength < 11 && dbf[at + nameLength] != 0) nameLength++;
            names.Add(Encoding.ASCII.GetString(dbf, at, nameLength));
            widths.Add(dbf[at + 16]);
        }
        if (names.Count == 0) return rows;

        // The .cpg beside these files says UTF-8, and UTF8Encoding without a throwing
        // decoder degrades an unexpected byte to a replacement character rather than
        // failing the whole read.
        var text = new UTF8Encoding(false, false);

        for (int r = 0; r < recordCount; r++)
        {
            int at = headerBytes + r * recordBytes;
            if (at + recordBytes > dbf.Length) break;
            if (dbf[at] == (byte)'*') continue; // tombstoned

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int field = at + 1;
            for (int f = 0; f < names.Count; f++)
            {
                if (field + widths[f] > dbf.Length) break;
                row[names[f]] = text.GetString(dbf, field, widths[f]).Trim();
                field += widths[f];
            }
            rows.Add(row);
        }

        return rows;
    }
}
