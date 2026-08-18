namespace OpenWSR.Palettes;

public readonly record struct Rgba(byte R, byte G, byte B, byte A);

/// <summary>
/// A color table: value-keyed stops interpolated into the 256-entry RGBA ramp the
/// renderer samples. Phase 7 adds GR2Analyst .pal parsing into this same shape.
/// </summary>
public sealed class ColorTable
{
    public required string Name { get; init; }
    public required float MinValue { get; init; }
    public required float MaxValue { get; init; }
    public required IReadOnlyList<(float Value, Rgba Color)> Stops { get; init; }

    /// <summary>Interpolation between stops; step tables (classic NWS look) use false.</summary>
    public bool Interpolate { get; init; } = true;

    public float Range => MaxValue - MinValue;

    /// <summary>256 RGBA entries covering [MinValue, MaxValue].</summary>
    public byte[] BuildRgba256()
    {
        var output = new byte[256 * 4];
        var stops = Stops.OrderBy(s => s.Value).ToArray();
        for (int i = 0; i < 256; i++)
        {
            float v = MinValue + Range * i / 255f;
            var c = Sample(stops, v);
            output[i * 4 + 0] = c.R;
            output[i * 4 + 1] = c.G;
            output[i * 4 + 2] = c.B;
            output[i * 4 + 3] = c.A;
        }
        return output;
    }

    private Rgba Sample((float Value, Rgba Color)[] stops, float v)
    {
        if (v <= stops[0].Value) return stops[0].Color;
        if (v >= stops[^1].Value) return stops[^1].Color;
        for (int i = 1; i < stops.Length; i++)
        {
            if (v > stops[i].Value) continue;
            if (!Interpolate) return stops[i - 1].Color;
            float t = (v - stops[i - 1].Value) / (stops[i].Value - stops[i - 1].Value);
            var a = stops[i - 1].Color;
            var b = stops[i].Color;
            return new Rgba(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t),
                (byte)(a.A + (b.A - a.A) * t));
        }
        return stops[^1].Color;
    }
}

/// <summary>Built-in tables modeled on the familiar NWS product colors.</summary>
public static class BuiltinTables
{
    public static ColorTable Reflectivity { get; } = new()
    {
        Name = "Reflectivity (dBZ)",
        MinValue = -30,
        MaxValue = 75,
        Stops =
        [
            (-30, new Rgba(0x00, 0x00, 0x00, 0x00)),
            (-10, new Rgba(0x40, 0x4A, 0x59, 0x60)),
            (5,   new Rgba(0x33, 0x64, 0x70, 0xB0)),
            (10,  new Rgba(0x41, 0xC0, 0xF0, 0xFF)),
            (18,  new Rgba(0x2E, 0x77, 0xEE, 0xFF)),
            (22,  new Rgba(0x2A, 0xFA, 0x30, 0xFF)),
            (35,  new Rgba(0x0E, 0x8E, 0x12, 0xFF)),
            (40,  new Rgba(0xFF, 0xFB, 0x1F, 0xFF)),
            (48,  new Rgba(0xFF, 0xA6, 0x00, 0xFF)),
            (50,  new Rgba(0xFF, 0x27, 0x0F, 0xFF)),
            (60,  new Rgba(0xA4, 0x0B, 0x0B, 0xFF)),
            (65,  new Rgba(0xFF, 0x2F, 0xF3, 0xFF)),
            (70,  new Rgba(0x9B, 0x55, 0xE0, 0xFF)),
            (75,  new Rgba(0xFF, 0xFF, 0xFF, 0xFF)),
        ],
    };

    public static ColorTable Velocity { get; } = new()
    {
        Name = "Velocity (m/s)",
        MinValue = -35,
        MaxValue = 35,
        Stops =
        [
            (-35, new Rgba(0x0B, 0x61, 0x0B, 0xFF)),
            (-20, new Rgba(0x1E, 0xC2, 0x1E, 0xFF)),
            (-2,  new Rgba(0xAF, 0xE3, 0xAF, 0xFF)),
            (0,   new Rgba(0x8E, 0x8E, 0x9E, 0xB0)),
            (2,   new Rgba(0xF0, 0xC0, 0xC0, 0xFF)),
            (20,  new Rgba(0xE0, 0x30, 0x30, 0xFF)),
            (35,  new Rgba(0x8A, 0x0A, 0x0A, 0xFF)),
        ],
    };

    public static ColorTable SpectrumWidth { get; } = new()
    {
        Name = "Spectrum width (m/s)",
        MinValue = 0,
        MaxValue = 15,
        Stops =
        [
            (0,  new Rgba(0x20, 0x28, 0x30, 0x80)),
            (4,  new Rgba(0x3C, 0x8B, 0xC8, 0xFF)),
            (8,  new Rgba(0xF2, 0xE3, 0x2A, 0xFF)),
            (12, new Rgba(0xF2, 0x54, 0x1E, 0xFF)),
            (15, new Rgba(0xFF, 0xFF, 0xFF, 0xFF)),
        ],
    };

    public static ColorTable DifferentialReflectivity { get; } = new()
    {
        Name = "ZDR (dB)",
        MinValue = -4,
        MaxValue = 8,
        Stops =
        [
            (-4, new Rgba(0x28, 0x28, 0x3C, 0xC0)),
            (0,  new Rgba(0x5A, 0x8C, 0xB4, 0xFF)),
            (1,  new Rgba(0x2A, 0xC8, 0x2A, 0xFF)),
            (2,  new Rgba(0xF2, 0xE3, 0x2A, 0xFF)),
            (4,  new Rgba(0xF2, 0x54, 0x1E, 0xFF)),
            (8,  new Rgba(0xC8, 0x28, 0xC8, 0xFF)),
        ],
    };

    public static ColorTable CorrelationCoefficient { get; } = new()
    {
        Name = "RhoHV",
        MinValue = 0.2f,
        MaxValue = 1.05f,
        Stops =
        [
            (0.20f, new Rgba(0x20, 0x20, 0x28, 0xC0)),
            (0.70f, new Rgba(0x3C, 0x64, 0xC8, 0xFF)),
            (0.90f, new Rgba(0x2A, 0xC8, 0x2A, 0xFF)),
            (0.97f, new Rgba(0xF2, 0xE3, 0x2A, 0xFF)),
            (1.00f, new Rgba(0xF2, 0x54, 0x1E, 0xFF)),
            (1.05f, new Rgba(0xC8, 0x28, 0xC8, 0xFF)),
        ],
    };

    public static ColorTable DifferentialPhase { get; } = new()
    {
        Name = "PhiDP (deg)",
        MinValue = 0,
        MaxValue = 360,
        Stops =
        [
            (0,   new Rgba(0x20, 0x28, 0x30, 0xC0)),
            (90,  new Rgba(0x3C, 0x8B, 0xC8, 0xFF)),
            (180, new Rgba(0x2A, 0xC8, 0x2A, 0xFF)),
            (270, new Rgba(0xF2, 0xE3, 0x2A, 0xFF)),
            (360, new Rgba(0xC8, 0x28, 0x28, 0xFF)),
        ],
    };

    public static ColorTable For(OpenWSR.Nexrad.Moment moment) => moment switch
    {
        OpenWSR.Nexrad.Moment.Velocity => Velocity,
        OpenWSR.Nexrad.Moment.SpectrumWidth => SpectrumWidth,
        OpenWSR.Nexrad.Moment.DifferentialReflectivity => DifferentialReflectivity,
        OpenWSR.Nexrad.Moment.CorrelationCoefficient => CorrelationCoefficient,
        OpenWSR.Nexrad.Moment.DifferentialPhase => DifferentialPhase,
        _ => Reflectivity,
    };
}
