using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenWSR.Nexrad.Analysis;
using OpenWSR.Palettes;

namespace OpenWSR.App;

/// <summary>Paints a cross-section with the same colour table the map is using.</summary>
public static class CrossSectionImage
{
    public static BitmapSource Render(CrossSectionResult section, ColorTable table)
    {
        var palette = table.BuildRgba256();
        float min = table.MinValue;
        float invRange = 1f / table.Range;

        int width = section.Width, height = section.Height;
        var pixels = new byte[width * height * 4];

        for (int i = 0; i < width * height; i++)
        {
            float value = section.Values[i];
            int target = i * 4;
            if (float.IsNaN(value))
            {
                // Unsampled space reads as the panel's own background, not as "no echo".
                pixels[target + 0] = 0x1B;
                pixels[target + 1] = 0x1F;
                pixels[target + 2] = 0x27;
                pixels[target + 3] = 0xFF;
                continue;
            }
            int level = (int)Math.Clamp((value - min) * invRange * 255f, 0, 255);
            int source = level * 4;
            pixels[target + 0] = palette[source + 2]; // B
            pixels[target + 1] = palette[source + 1]; // G
            pixels[target + 2] = palette[source + 0]; // R
            pixels[target + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
