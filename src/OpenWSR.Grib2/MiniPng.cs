using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenWSR.Grib2;

/// <summary>
/// Just enough PNG to read GRIB2 template 5.41 payloads: non-interlaced greyscale at
/// 8 or 16 bits. Keeping it here means the GRIB2 library needs no imaging dependency.
/// </summary>
internal static class MiniPng
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decode to one unsigned sample per pixel, row-major.</summary>
    public static uint[] DecodeGrayscale(ReadOnlySpan<byte> png, out int width, out int height)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature))
            throw new Grib2FormatException("PNG-packed data is missing its signature.");

        width = height = 0;
        int bitDepth = 0, colourType = 0;
        var idat = new MemoryStream();
        int pos = 8;

        while (pos + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[pos..]);
            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, length);

            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colourType = data[9];
                if (data[12] != 0)
                    throw new Grib2FormatException("Interlaced PNG is not supported.");
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
            pos += 12 + length; // length + type + data + CRC
        }

        if (colourType != 0 || (bitDepth != 8 && bitDepth != 16))
            throw new Grib2FormatException(
                $"Expected 8- or 16-bit greyscale PNG, got colour type {colourType} depth {bitDepth}.");

        idat.Position = 0;
        using var inflate = new ZLibStream(idat, CompressionMode.Decompress);
        int bytesPerSample = bitDepth / 8;
        int stride = width * bytesPerSample;
        var previous = new byte[stride];
        var current = new byte[stride];
        var samples = new uint[width * height];

        for (int row = 0; row < height; row++)
        {
            int filter = inflate.ReadByte();
            if (filter < 0) throw new Grib2FormatException("PNG data ended early.");
            inflate.ReadExactly(current, 0, stride);
            Unfilter(filter, current, previous, bytesPerSample);

            for (int x = 0; x < width; x++)
            {
                samples[row * width + x] = bytesPerSample == 1
                    ? current[x]
                    : (uint)((current[x * 2] << 8) | current[x * 2 + 1]);
            }
            (previous, current) = (current, previous);
        }
        return samples;
    }

    private static void Unfilter(int filter, byte[] line, byte[] previous, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1: // Sub
                for (int i = bpp; i < line.Length; i++)
                    line[i] = (byte)(line[i] + line[i - bpp]);
                break;
            case 2: // Up
                for (int i = 0; i < line.Length; i++)
                    line[i] = (byte)(line[i] + previous[i]);
                break;
            case 3: // Average
                for (int i = 0; i < line.Length; i++)
                {
                    int left = i >= bpp ? line[i - bpp] : 0;
                    line[i] = (byte)(line[i] + ((left + previous[i]) >> 1));
                }
                break;
            case 4: // Paeth
                for (int i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0;
                    int b = previous[i];
                    int c = i >= bpp ? previous[i - bpp] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    line[i] = (byte)(line[i] + pred);
                }
                break;
            default:
                throw new Grib2FormatException($"Unknown PNG filter {filter}.");
        }
    }
}
