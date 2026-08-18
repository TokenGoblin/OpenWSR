using System.IO.Compression;
using OpenWSR.Nexrad.Internal;

namespace OpenWSR.Nexrad.Level3;

/// <summary>
/// Decoder for graphic-overlay Level III (NIDS) products, per ICD 2620001:
/// NST storm tracks (58), NHI hail index (59), NMD mesocyclone detection (141).
/// Layout: text WMO header, 18-byte message header, 102-byte product description
/// block, then the symbology block (zlib-compressed on newer builds) holding
/// display packets. Radial-image products are out of scope here — Level II is the
/// image source in OpenWSR; Level III supplies algorithm overlays.
/// </summary>
public static class Level3File
{
    /// <summary>Strip the WMO text header: returns the binary message and the 3-letter site.</summary>
    internal static (byte[] Msg, string SiteId) OpenMessage(ReadOnlySpan<byte> file)
    {
        // Text header: "SDUS34 KOUN 110149\r\r\n NMDTLX\r\r\n" — variable length.
        int headerEnd = FindSecondCrCrLf(file);
        string siteId = Be.Ascii(file, headerEnd - 6, 3); // "…NSTTLX\r\r\n"
        var msg = file[headerEnd..];
        if (msg.Length < 120)
            throw new NexradFormatException("Level III message truncated.");
        return (msg.ToArray(), siteId);
    }

    public static Level3Product Decode(ReadOnlySpan<byte> file)
    {
        var (msgArray, siteId) = OpenMessage(file);
        ReadOnlySpan<byte> msg = msgArray;

        int productCode = Be.I16(msg, 0);

        // Product description block (starts at byte 18 with an 0xFFFF divider).
        if (Be.I16(msg, 18) != -1)
            throw new NexradFormatException("Level III product description block divider missing.");
        double lat = Be.I32(msg, 20) / 1000.0;
        double lon = Be.I32(msg, 24) / 1000.0;
        double heightFt = Be.I16(msg, 28);
        int mode = Be.I16(msg, 32);
        int vcp = Be.I16(msg, 34);
        var volumeTime = NexradTime.FromJulian(Be.I16(msg, 40), Be.U32(msg, 42) * 1000);
        var productTime = NexradTime.FromJulian(Be.I16(msg, 46), Be.U32(msg, 48) * 1000);

        int symbologyOffset = checked((int)(Be.U32(msg, 108) * 2));
        var storms = new List<StormCell>();
        var hail = new List<HailIndicator>();
        var mesos = new List<MesocycloneDetection>();
        var structures = new List<StormCellStructure>();

        if (symbologyOffset > 0 && symbologyOffset < msg.Length)
        {
            var symbology = DecompressBlock(msg[symbologyOffset..]);
            ParseSymbology(symbology, storms, hail, mesos);
        }

        // Stand-alone tabular products (NSS) carry the tabular block at the
        // symbology-offset slot; others use the dedicated tabular offset.
        int tabularOffset = checked((int)(Be.U32(msg, 116) * 2));
        if (tabularOffset <= 0)
            tabularOffset = symbologyOffset;
        if (productCode == 62 && tabularOffset > 0 && tabularOffset < msg.Length)
            ParseStormStructureTable(ReadTabularLines(msg[tabularOffset..]), structures);

        return new Level3Product(
            productCode, siteId, lat, lon, heightFt, vcp, mode,
            volumeTime, productTime, storms, hail, mesos, structures);
    }

    public static Level3Product DecodeFile(string path) => Decode(File.ReadAllBytes(path));

    private static int FindSecondCrCrLf(ReadOnlySpan<byte> file)
    {
        int seen = 0;
        for (int i = 0; i < Math.Min(file.Length - 2, 120); i++)
        {
            if (file[i] == '\r' && file[i + 1] == '\r' && file[i + 2] == '\n' && ++seen == 2)
                return i + 3;
        }
        throw new NexradFormatException("Level III WMO text header not found.");
    }

    internal static byte[] DecompressBlock(ReadOnlySpan<byte> block)
    {
        if (block.Length >= 3 && block[0] == 0x78) // zlib
        {
            using var input = new MemoryStream(block.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(block.Length * 8);
            zlib.CopyTo(output);
            return output.ToArray();
        }
        if (block.Length >= 3 && block[0] == (byte)'B' && block[1] == (byte)'Z' && block[2] == (byte)'h')
        {
            using var input = new MemoryStream(block.ToArray(), writable: false);
            using var bz = new ICSharpCode.SharpZipLib.BZip2.BZip2InputStream(input);
            using var output = new MemoryStream(block.Length * 8);
            bz.CopyTo(output);
            return output.ToArray();
        }
        return block.ToArray();
    }

    // ---- symbology block + display packets ----

    private static void ParseSymbology(
        ReadOnlySpan<byte> s,
        List<StormCell> storms, List<HailIndicator> hail, List<MesocycloneDetection> mesos)
    {
        // Some products carry no symbology; the offset then lands on another block type.
        if (s.Length < 10 || Be.I16(s, 0) != -1 || Be.I16(s, 2) != 1)
            return;
        int layerCount = Be.I16(s, 8);
        int pos = 10;

        // Per-feature accumulation: packets arrive grouped per storm/feature in order.
        string? pendingId = null;
        KmPoint? pendingPos = null;
        List<KmPoint> past = [], forecast = [];
        double pendingRadius = 0;
        int pendingType = 0;
        bool isMeso = false;

        void FlushFeature()
        {
            if (pendingPos is not { } position) return;
            if (isMeso)
                mesos.Add(new MesocycloneDetection(
                    pendingId?.Trim(), position, pendingRadius, pendingType, [.. past], [.. forecast]));
            else
                storms.Add(new StormCell(pendingId?.Trim() ?? "?", position, [.. past], [.. forecast]));
            pendingId = null;
            pendingPos = null;
            pendingRadius = 0;
            pendingType = 0;
            isMeso = false;
            past = [];
            forecast = [];
        }

        for (int layer = 0; layer < layerCount && pos + 6 <= s.Length; layer++)
        {
            int layerLength = checked((int)Be.U32(s, pos + 2));
            pos += 6;
            int layerEnd = Math.Min(pos + layerLength, s.Length);

            while (pos + 4 <= layerEnd)
            {
                int code = Be.I16(s, pos);
                int length = Be.U16(s, pos + 2);
                var body = s.Slice(pos + 4, Math.Min(length, layerEnd - pos - 4));
                pos += 4 + length;

                switch (code)
                {
                    case 2 when body.Length >= 4:
                        // Special symbol marking the current position starts a new feature.
                        FlushFeature();
                        pendingPos = Point(body, 0);
                        break;

                    case 15: // storm ID: I, J, two characters — repeats
                        for (int o = 0; o + 6 <= body.Length; o += 6)
                        {
                            var p = Point(body, o);
                            string id = Be.Ascii(body, o + 4, 2);
                            if (pendingPos is { } current &&
                                Math.Abs(current.XKm - p.XKm) < 0.01 && Math.Abs(current.YKm - p.YKm) < 0.01)
                            {
                                pendingId = id; // label for the feature in progress
                            }
                            else
                            {
                                FlushFeature();
                                pendingPos = p;
                                pendingId = id;
                            }
                        }
                        break;

                    case 19: // HDA hail: I, J, POH, POSH, max size — repeats
                        for (int o = 0; o + 10 <= body.Length; o += 10)
                        {
                            hail.Add(new HailIndicator(
                                null, Point(body, o),
                                Be.I16(body, o + 4), Be.I16(body, o + 6), Be.I16(body, o + 8)));
                        }
                        break;

                    case 20 when body.Length >= 8: // point feature: I, J, type, radius attribute
                        FlushFeature();
                        isMeso = true;
                        pendingPos = Point(body, 0);
                        pendingType = Be.I16(body, 4);
                        pendingRadius = Be.I16(body, 6) * 0.25;
                        break;

                    case 8 when body.Length >= 6:
                        // Text-with-value packet: color, I, J, chars — labels the open feature.
                        if (isMeso && pendingPos is not null)
                            pendingId = Be.Ascii(body, 6, body.Length - 6);
                        break;

                    case 23: // SCIT past data: embedded packets
                        ParseScit(body, past);
                        break;

                    case 24: // SCIT forecast data: embedded packets
                        ParseScit(body, forecast);
                        break;
                }
            }
        }
        FlushFeature();

        // NHI files carry storm IDs as separate packet-15 groups; attach them to hail
        // detections at the same position.
        if (hail.Count > 0 && storms.Count > 0)
        {
            for (int i = 0; i < hail.Count; i++)
            {
                var match = storms.FirstOrDefault(c =>
                    Math.Abs(c.Position.XKm - hail[i].Position.XKm) < 0.01 &&
                    Math.Abs(c.Position.YKm - hail[i].Position.YKm) < 0.01);
                if (match is not null)
                    hail[i] = hail[i] with { StormId = match.Id };
            }
            storms.Clear(); // ID-carrier cells in NHI are labels, not tracked storms
        }
    }

    /// <summary>Packets 23/24 wrap ordinary packets (2 = marker, 6 = linked vector, 25 = circle).</summary>
    private static void ParseScit(ReadOnlySpan<byte> body, List<KmPoint> positions)
    {
        int pos = 0;
        while (pos + 4 <= body.Length)
        {
            int code = Be.I16(body, pos);
            int length = Be.U16(body, pos + 2);
            var inner = body.Slice(pos + 4, Math.Min(length, body.Length - pos - 4));
            pos += 4 + length;

            switch (code)
            {
                case 2 when inner.Length >= 4: // position marker
                    positions.Add(Point(inner, 0));
                    break;
                case 25 when inner.Length >= 4: // circle: centre is the position
                    positions.Add(Point(inner, 0));
                    break;
                // code 6 (linked vectors) draws the connecting line between the same
                // positions the markers carry — nothing extra to extract.
            }
        }
    }

    private static KmPoint Point(ReadOnlySpan<byte> s, int offset) =>
        new(Be.I16(s, offset) * 0.25, Be.I16(s, offset + 2) * 0.25);

    // ---- tabular alphanumeric block ----

    /// <summary>
    /// Tabular block: divider, id=3, length, an embedded message-header + PDB copy,
    /// then a divider, page count, and pages of [charCount][chars] lines, each page
    /// terminated by a -1 divider.
    /// </summary>
    private static List<string> ReadTabularLines(ReadOnlySpan<byte> block)
    {
        var lines = new List<string>();
        if (block.Length < 8 || Be.I16(block, 0) != -1)
            return lines;

        int pages, pos;
        if (Be.I16(block, 2) == 3)
        {
            // Attached tabular block: id=3, length, embedded message header + PDB copy.
            pos = 8 + 18 + 102;
            if (pos + 4 > block.Length || Be.I16(block, pos) != -1)
                return lines;
            pages = Be.I16(block, pos + 2);
            pos += 4;
        }
        else
        {
            // Stand-alone tabular (e.g. NSS): divider, page count, pages directly.
            pages = Be.I16(block, 2);
            pos = 4;
        }
        if (pages is <= 0 or > 64)
            return lines;

        for (int page = 0; page < pages && pos + 2 <= block.Length; page++)
        {
            while (pos + 2 <= block.Length)
            {
                int count = Be.I16(block, pos);
                pos += 2;
                if (count == -1) break; // end of page
                if (count < 0 || pos + count > block.Length) return lines;
                lines.Add(Be.Ascii(block, pos, count));
                pos += count;
            }
        }
        return lines;
    }

    /// <summary>NSS rows: "R5  182/ 95  &lt;11.1  50.1  37  52  32.5" (id, azran, base, top, VIL, max dBZ, height).</summary>
    private static void ParseStormStructureTable(List<string> lines, List<StormCellStructure> structures)
    {
        var row = new System.Text.RegularExpressions.Regex(
            @"^\s*([A-Z][0-9])\s+(\d+)/\s*(\d+)\s+[<>]?\s*([\d.]+)\s+[<>]?\s*([\d.]+)\s+([\d.]+)\s+(\d+)\s+([\d.]+)\s*$");
        foreach (var line in lines)
        {
            var match = row.Match(line);
            if (!match.Success) continue;
            double F(int group) => double.Parse(
                match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
            structures.Add(new StormCellStructure(
                match.Groups[1].Value,
                F(2), F(3),
                int.Parse(match.Groups[7].Value),
                F(6), F(4), F(5), F(8)));
        }
    }
}
