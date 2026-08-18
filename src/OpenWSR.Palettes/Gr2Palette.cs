using System.Globalization;

namespace OpenWSR.Palettes;

/// <summary>
/// GR2Analyst .pal color table parser — the community-standard palette format.
/// Recognized directives (case-insensitive):
///   Product/Units:   informational, kept for the table name
///   Scale/Offset:    value transform (value*scale + offset)
///   Color:  v r g b [r2 g2 b2]      gradient band (to second color, else next band)
///   Color4: v r g b a [r2 g2 b2 a2] gradient band with alpha
///   SolidColor[4]: v r g b [a]      constant band
///   RF: r g b [a]                    range-folded color
/// Comments start with ';'. Unknown directives are ignored.
/// </summary>
public static class Gr2Palette
{
    public static ColorTable Parse(string text, string? name = null)
    {
        string? product = null, units = null;
        float scale = 1f, offset = 0f;
        Rgba? rangeFolded = null;
        var entries = new List<PaletteEntry>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Split(';')[0].Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var directive = line[..colon].Trim().ToLowerInvariant();
            var rest = line[(colon + 1)..].Trim();
            var tokens = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            switch (directive)
            {
                case "product": product = rest; break;
                case "units": units = rest; break;
                case "scale": scale = F(tokens[0]); break;
                case "offset": offset = F(tokens[0]); break;
                case "step": break; // legend rendering hint only
                case "rf":
                    rangeFolded = ParseColor(tokens, 0, tokens.Length >= 4);
                    break;
                case "color":
                    entries.Add(ParseBand(tokens, alpha: false, solid: false, scale, offset));
                    break;
                case "color4":
                    entries.Add(ParseBand(tokens, alpha: true, solid: false, scale, offset));
                    break;
                case "solidcolor":
                    entries.Add(ParseBand(tokens, alpha: false, solid: true, scale, offset));
                    break;
                case "solidcolor4":
                    entries.Add(ParseBand(tokens, alpha: true, solid: true, scale, offset));
                    break;
            }
        }

        if (entries.Count < 2)
            throw new FormatException("Palette defines fewer than two color bands.");

        return new ColorTable
        {
            Name = name ?? (product is null ? "Imported palette" : $"{product} ({units})"),
            Entries = entries.OrderBy(e => e.Value).ToList(),
            RangeFolded = rangeFolded,
        };
    }

    public static ColorTable ParseFile(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

    private static PaletteEntry ParseBand(string[] t, bool alpha, bool solid, float scale, float offset)
    {
        float value = F(t[0]) * scale + offset;
        int stride = alpha ? 4 : 3;
        var start = ParseColor(t, 1, alpha);
        Rgba? end = t.Length >= 1 + 2 * stride ? ParseColor(t, 1 + stride, alpha) : null;
        return new PaletteEntry(value, start, solid ? null : end, solid);
    }

    private static Rgba ParseColor(string[] t, int index, bool alpha) => new(
        B(t[index]), B(t[index + 1]), B(t[index + 2]),
        alpha && t.Length > index + 3 ? B(t[index + 3]) : (byte)255);

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    private static byte B(string s) => (byte)Math.Clamp((int)F(s), 0, 255);
}
