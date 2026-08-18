namespace OpenWSR.Palettes;

public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public static Rgba Lerp(Rgba a, Rgba b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t),
        (byte)(a.A + (b.A - a.A) * t));
}

/// <summary>
/// One palette band: at <paramref name="Value"/> the color is <paramref name="Start"/>.
/// Toward the next band's value it blends to <paramref name="End"/> when set, to the next
/// band's start color when interpolating, or stays constant when <paramref name="Solid"/>.
/// This is exactly the GR2Analyst .pal model; built-in tables use the same shape.
/// </summary>
public readonly record struct PaletteEntry(float Value, Rgba Start, Rgba? End = null, bool Solid = false);

/// <summary>A color table compiled into the 256-entry RGBA ramp the renderer samples.</summary>
public sealed class ColorTable
{
    public required string Name { get; init; }
    public required IReadOnlyList<PaletteEntry> Entries { get; init; }

    /// <summary>Range-folded color, when the table defines one (.pal RF: line).</summary>
    public Rgba? RangeFolded { get; init; }

    public float MinValue => Entries.Min(e => e.Value);
    public float MaxValue => Entries.Max(e => e.Value);
    public float Range => MaxValue - MinValue;

    public static ColorTable FromStops(string name, params (float Value, Rgba Color)[] stops) => new()
    {
        Name = name,
        Entries = [.. stops.Select(s => new PaletteEntry(s.Value, s.Color))],
    };

    /// <summary>256 RGBA entries covering [MinValue, MaxValue].</summary>
    public byte[] BuildRgba256()
    {
        var entries = Entries.OrderBy(e => e.Value).ToArray();
        var output = new byte[256 * 4];
        float min = entries[0].Value, range = entries[^1].Value - entries[0].Value;
        for (int i = 0; i < 256; i++)
        {
            var c = Sample(entries, min + range * i / 255f);
            output[i * 4 + 0] = c.R;
            output[i * 4 + 1] = c.G;
            output[i * 4 + 2] = c.B;
            output[i * 4 + 3] = c.A;
        }
        return output;
    }

    private static Rgba Sample(PaletteEntry[] entries, float v)
    {
        if (v <= entries[0].Value) return entries[0].Start;
        if (v >= entries[^1].Value)
        {
            var last = entries[^1];
            return last.Solid || last.End is null ? last.Start : last.End.Value;
        }
        for (int i = 0; i < entries.Length - 1; i++)
        {
            if (v > entries[i + 1].Value) continue;
            var band = entries[i];
            if (band.Solid) return band.Start;
            float t = (v - band.Value) / (entries[i + 1].Value - band.Value);
            return Rgba.Lerp(band.Start, band.End ?? entries[i + 1].Start, t);
        }
        return entries[^1].Start;
    }
}
