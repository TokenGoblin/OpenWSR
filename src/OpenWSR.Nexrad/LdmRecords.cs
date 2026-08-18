using System.Buffers.Binary;
using ICSharpCode.SharpZipLib.BZip2;

namespace OpenWSR.Nexrad;

/// <summary>
/// Splits an Archive II byte stream into decompressed LDM records.
/// Each record is a 4-byte big-endian control word (absolute value = compressed byte
/// length; a negative value marks the final record of the volume) followed by a bzip2
/// stream. Real-time I/E chunks are bare sequences of these records with no volume header.
/// </summary>
public static class LdmRecords
{
    public static IEnumerable<byte[]> Enumerate(Stream stream)
    {
        var controlWord = new byte[4];
        while (true)
        {
            int got = ReadAtMost(stream, controlWord);
            if (got == 0)
                yield break; // clean end of stream
            if (got < 4)
                throw new NexradFormatException("Truncated LDM control word.");

            int size = BinaryPrimitives.ReadInt32BigEndian(controlWord);
            bool final = size < 0;
            size = Math.Abs(size);
            if (size == 0)
            {
                if (final) yield break;
                continue;
            }

            var compressed = new byte[size];
            if (ReadAtMost(stream, compressed) < size)
                throw new NexradFormatException("Truncated LDM record body.");

            yield return Decompress(compressed);

            if (final)
                yield break;
        }
    }

    private static byte[] Decompress(byte[] compressed)
    {
        if (compressed.Length >= 3 && compressed[0] == (byte)'B' && compressed[1] == (byte)'Z' && compressed[2] == (byte)'h')
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var bz = new BZip2InputStream(input);
            using var output = new MemoryStream(compressed.Length * 8);
            bz.CopyTo(output);
            return output.ToArray();
        }
        return compressed; // uncompressed record (rare, but the control word permits it)
    }

    private static int ReadAtMost(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
