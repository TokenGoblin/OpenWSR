using System.IO.Compression;

namespace OpenWSR.Nexrad;

/// <summary>Top-level decoder for a complete Archive II file (optionally gzip-wrapped, pre-2016).</summary>
public static class ArchiveFile
{
    public static RadarVolume Decode(Stream stream)
    {
        var bytes = ReadAll(stream);
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(bytes, writable: false), CompressionMode.Decompress);
            bytes = ReadAll(gz);
        }

        if (bytes.Length < VolumeHeader.Size)
            throw new NexradFormatException("File shorter than a volume header.");
        VolumeHeader.Parse(bytes); // validates the AR2V signature

        var builder = new VolumeBuilder();
        // Two historical layouts follow the 24-byte header:
        //  - modern: LDM records (4-byte control word, then a bzip2 stream — "BZh")
        //  - gzip-era: raw uncompressed messages with no record framing at all
        if (bytes.Length >= 31 && bytes[28] == (byte)'B' && bytes[29] == (byte)'Z')
        {
            using var body = new MemoryStream(bytes, VolumeHeader.Size,
                bytes.Length - VolumeHeader.Size, writable: false);
            foreach (var record in LdmRecords.Enumerate(body))
                builder.AddRecord(record);
        }
        else
        {
            builder.AddRecord(bytes.AsSpan(VolumeHeader.Size));
        }
        return builder.Build();
    }

    public static RadarVolume DecodeFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Decode(fs);
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out _))
            return ms.ToArray();
        using var buffer = new MemoryStream(
            stream.CanSeek ? checked((int)(stream.Length - stream.Position)) : 1 << 20);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
