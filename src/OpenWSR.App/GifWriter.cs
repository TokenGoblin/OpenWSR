using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenWSR.App;

/// <summary>
/// Saves captured map frames. WPF's GIF encoder writes the frames but none of the
/// animation metadata, so the Netscape looping block and per-frame delays are patched
/// in afterwards — otherwise viewers show a still image.
/// </summary>
public static class GifWriter
{
    public static void SavePng(string path, byte[] bgra, int width, int height)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmap(bgra, width, height)));
        encoder.Save(stream);
    }

    public static void Save(
        string path, IReadOnlyList<(byte[] Bgra, int Width, int Height)> frames,
        int delayCentiseconds = 25)
    {
        using var buffer = new MemoryStream();
        var encoder = new GifBitmapEncoder();
        foreach (var (bgra, width, height) in frames)
            encoder.Frames.Add(BitmapFrame.Create(ToBitmap(bgra, width, height)));
        encoder.Save(buffer);

        var bytes = buffer.ToArray();
        using var output = File.Create(path);
        WriteAnimated(output, bytes, delayCentiseconds);
    }

    private static BitmapSource ToBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Rewrite the encoder's output, inserting the application extension that makes it
    /// loop and a graphic control extension before each image so frames have a duration.
    /// </summary>
    private static void WriteAnimated(Stream output, byte[] gif, int delayCentiseconds)
    {
        // Header (6) + logical screen descriptor (7) and the global colour table if present.
        int pos = 6;
        byte packed = gif[pos + 4];
        pos += 7;
        if ((packed & 0x80) != 0)
            pos += 3 * (1 << ((packed & 0x07) + 1));

        output.Write(gif, 0, pos);

        // NETSCAPE2.0 application extension: loop forever.
        output.Write([
            0x21, 0xFF, 0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
            (byte)'2', (byte)'.', (byte)'0',
            0x03, 0x01, 0x00, 0x00, 0x00,
        ]);

        while (pos < gif.Length)
        {
            byte block = gif[pos];
            if (block == 0x3B) break; // trailer

            if (block == 0x21) // existing extension — skip it, we write our own
            {
                pos += 2;
                pos = SkipSubBlocks(gif, pos);
                continue;
            }

            if (block == 0x2C) // image descriptor
            {
                output.Write([
                    0x21, 0xF9, 0x04, 0x00,
                    (byte)(delayCentiseconds & 0xFF), (byte)((delayCentiseconds >> 8) & 0xFF),
                    0x00, 0x00,
                ]);

                int start = pos;
                pos += 10;
                byte imagePacked = gif[start + 9];
                if ((imagePacked & 0x80) != 0)
                    pos += 3 * (1 << ((imagePacked & 0x07) + 1));
                pos += 1; // LZW minimum code size
                pos = SkipSubBlocks(gif, pos);
                output.Write(gif, start, pos - start);
                continue;
            }
            pos++;
        }
        output.WriteByte(0x3B);
    }

    private static int SkipSubBlocks(byte[] data, int pos)
    {
        while (pos < data.Length && data[pos] != 0)
            pos += data[pos] + 1;
        return pos + 1;
    }
}
