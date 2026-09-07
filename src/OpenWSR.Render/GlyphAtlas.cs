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

    /// <summary>Type size for a glyph that fits its cell without help.</summary>
    private const double NominalSize = 19;

    /// <summary>
    /// Pixels of guaranteed clearance inside each cell, so a glyph scaled to fit still stops
    /// short of the edge — and so a test can assert that nothing reaches its neighbour.
    /// </summary>
    private const double Margin = 2;

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
                var cell = new Rect(
                    i % Columns * CellWidth, i / Columns * CellHeight, CellWidth, CellHeight);

                var text = Measured(Charset[i], typeface, NominalSize);

                // A character the typeface does not carry comes back from font fallback at
                // whatever size *that* face draws it, which can be wider than the cell — and a
                // glyph wider than its cell, centred, spills into its neighbours. Consolas has
                // no warning sign, and the degree sign sits immediately to its left in the
                // charset, so every temperature on the map picked up a shard of the warning
                // triangle. Shrink to fit rather than clip, so an oversized glyph is still
                // itself.
                double room = Math.Min(
                    (CellWidth - Margin) / Math.Max(text.Width, 0.001),
                    (CellHeight - Margin) / Math.Max(text.Height, 0.001));
                if (room < 1.0) text = Measured(Charset[i], typeface, NominalSize * room);

                // And clipped anyway. The shrink is arithmetic on a measurement, and a font
                // that reports one and draws another would be back to bleeding into the cell
                // next door — which is invisible here and shows up as litter on the map.
                dc.PushClip(new RectangleGeometry(cell));
                dc.DrawText(text, new Point(
                    cell.X + (CellWidth - text.Width) / 2.0,
                    cell.Y + (CellHeight - text.Height) / 2.0));
                dc.Pop();
            }
        }

        var bitmap = new RenderTargetBitmap(TextureWidth, TextureHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Bgra = new byte[TextureWidth * TextureHeight * 4];
        bitmap.CopyPixels(Bgra, TextureWidth * 4, 0);
    }

    private static FormattedText Measured(char c, Typeface typeface, double size) =>
        new(c.ToString(), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, size, Brushes.White, 1.0);

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
