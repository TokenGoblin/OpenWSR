using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenWSR.Render;

/// <summary>
/// A monospace glyph atlas rendered once with WPF (UI thread) and uploaded as a
/// texture by the render thread. White glyphs on transparent — tint at draw time.
/// </summary>
public sealed class GlyphAtlas
{
    public const string Charset = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+-./%°⚠";
    public const int CellWidth = 14;
    public const int CellHeight = 24;
    public const int Columns = 16;

    public byte[] Bgra { get; }
    public int TextureWidth { get; }
    public int TextureHeight { get; }

    public GlyphAtlas()
    {
        int rows = (Charset.Length + Columns - 1) / Columns;
        TextureWidth = Columns * CellWidth;
        TextureHeight = rows * CellHeight;

        var visual = new DrawingVisual();
        var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal,
            FontWeights.Bold, FontStretches.Normal);
        using (var dc = visual.RenderOpen())
        {
            for (int i = 0; i < Charset.Length; i++)
            {
                var text = new FormattedText(
                    Charset[i].ToString(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 19, Brushes.White, 1.0);
                double x = i % Columns * CellWidth + (CellWidth - text.Width) / 2.0;
                double y = i / Columns * CellHeight + (CellHeight - text.Height) / 2.0;
                dc.DrawText(text, new Point(x, y));
            }
        }

        var bitmap = new RenderTargetBitmap(TextureWidth, TextureHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Bgra = new byte[TextureWidth * TextureHeight * 4];
        bitmap.CopyPixels(Bgra, TextureWidth * 4, 0);
    }

    /// <summary>UV rectangle of a character, or null when it is not in the charset.</summary>
    public (float U0, float V0, float U1, float V1)? UvFor(char c)
    {
        int index = Charset.IndexOf(char.ToUpperInvariant(c));
        if (index < 0) return null;
        float u0 = index % Columns * (float)CellWidth / TextureWidth;
        float v0 = index / Columns * (float)CellHeight / TextureHeight;
        return (u0, v0, u0 + (float)CellWidth / TextureWidth, v0 + (float)CellHeight / TextureHeight);
    }
}
