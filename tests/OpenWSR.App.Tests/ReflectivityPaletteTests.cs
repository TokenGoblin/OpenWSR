using OpenWSR.Palettes;

namespace OpenWSR.App.Tests;

/// <summary>
/// The reflectivity table's low end fades in so that clear-air return is context rather than
/// subject. The claim that makes it safe is that <em>weather</em> is untouched, so that is
/// what these assert.
/// </summary>
public sealed class ReflectivityPaletteTests
{
    private static readonly byte[] Ramp = BuiltinTables.Reflectivity.BuildRgba256();

    /// <summary>The 256-entry ramp index a dBZ value lands on.</summary>
    private static int IndexOf(double dbz)
    {
        var table = BuiltinTables.Reflectivity;
        return (int)Math.Clamp(
            (dbz - table.MinValue) / table.Range * 255.0, 0, 255);
    }

    private static (byte R, byte G, byte B, byte A) At(double dbz)
    {
        int i = IndexOf(dbz) * 4;
        return (Ramp[i], Ramp[i + 1], Ramp[i + 2], Ramp[i + 3]);
    }

    /// <summary>The table as it was before the low end was faded, for comparison.</summary>
    private static ColorTable BeforeTheFade() => ColorTable.FromStops("Reflectivity (dBZ)",
        (-30, new Rgba(0x00, 0x00, 0x00, 0x00)),
        (-10, new Rgba(0x40, 0x4A, 0x59, 0x60)),
        (5, new Rgba(0x33, 0x64, 0x70, 0xB0)),
        (10, new Rgba(0x41, 0xC0, 0xF0, 0xFF)),
        (18, new Rgba(0x2E, 0x77, 0xEE, 0xFF)),
        (22, new Rgba(0x2A, 0xFA, 0x30, 0xFF)),
        (35, new Rgba(0x0E, 0x8E, 0x12, 0xFF)),
        (40, new Rgba(0xFF, 0xFB, 0x1F, 0xFF)),
        (48, new Rgba(0xFF, 0xA6, 0x00, 0xFF)),
        (50, new Rgba(0xFF, 0x27, 0x0F, 0xFF)),
        (60, new Rgba(0xA4, 0x0B, 0x0B, 0xFF)),
        (65, new Rgba(0xFF, 0x2F, 0xF3, 0xFF)),
        (70, new Rgba(0x9B, 0x55, 0xE0, 0xFF)),
        (75, new Rgba(0xFF, 0xFF, 0xFF, 0xFF)));

    [Fact]
    public void WeatherIsByteForByteWhatItWasBeforeTheFade()
    {
        // The claim the whole change rests on. Every band from the green stop upward has
        // both endpoints untouched, so the two ramps must agree exactly there — not
        // "look similar", agree.
        var before = BeforeTheFade().BuildRgba256();
        int from = FirstIndexAtOrAbove(22);

        for (int i = from; i < 256; i++)
            for (int channel = 0; channel < 4; channel++)
                Assert.Equal(before[i * 4 + channel], Ramp[i * 4 + channel]);
    }

    [Fact]
    public void PrecipitationIsFullyOpaque()
    {
        // From the first ramp step at or above the green threshold. The single step that
        // straddles 22 dBZ lands at 21.88 and is still interpolating, which is quantisation
        // in the 256-entry ramp and predates this change.
        for (int i = FirstIndexAtOrAbove(22); i < 256; i++)
            Assert.Equal(255, Ramp[i * 4 + 3]);
    }

    private static int FirstIndexAtOrAbove(double dbz)
    {
        var table = BuiltinTables.Reflectivity;
        for (int i = 0; i < 256; i++)
            if (table.MinValue + table.Range * i / 255.0 >= dbz)
                return i;
        return 255;
    }

    [Fact]
    public void ClearAirReturnIsTranslucent()
    {
        // The band the blue strips live in. Opaque here is what put cyan over a quarter of
        // the screen; these are the values that make it recede instead.
        Assert.InRange(At(5).A, 0x10, 0x50);
        Assert.InRange(At(10).A, 0x40, 0x90);
        Assert.InRange(At(15).A, 0x80, 0xC0);
    }

    [Fact]
    public void NothingBelowNoiseIsDrawnAtAll()
    {
        Assert.Equal(0, At(-30).A);
        Assert.Equal(0, At(-10).A);
    }

    [Fact]
    public void OpacityRisesWithReflectivityAndNeverFallsBack()
    {
        // A dip anywhere would make a band of light rain read as *less* present than the
        // drizzle beneath it.
        int previous = -1;
        for (int i = 0; i < 256; i++)
        {
            int alpha = Ramp[i * 4 + 3];
            Assert.True(alpha >= previous, $"alpha fell at ramp index {i}");
            previous = alpha;
        }
    }

    [Fact]
    public void TheFadeIsFinishedBeforeAnythingWorthSeeing()
    {
        // Light rain starts around 20 dBZ. The ramp has to be done by then, or the product
        // would be hiding precipitation rather than clutter.
        Assert.Equal(255, Ramp[FirstIndexAtOrAbove(22) * 4 + 3]);
        Assert.True(At(18).A >= 0xB0, "light returns should be clearly visible, just not solid");
    }
}
