using OpenWSR.Palettes;

namespace OpenWSR.Nexrad.Tests;

public class Gr2PaletteTests
{
    private const string SamplePal = """
        ; Sample GR2Analyst reflectivity palette
        Product: BR
        Units: DBZ
        Step: 5
        RF: 119 0 125
        Color: 65 255 0 255 128 0 128
        Color: 50 255 0 0
        SolidColor: 40 255 200 0
        Color4: 20 0 200 0 128
        Color: 0 100 100 100
        """;

    [Fact]
    public void ParsesBandsSortedWithMetadata()
    {
        var table = Gr2Palette.Parse(SamplePal);
        Assert.Equal("BR (DBZ)", table.Name);
        Assert.Equal(5, table.Entries.Count);
        Assert.Equal(0, table.MinValue);
        Assert.Equal(65, table.MaxValue);
        Assert.Equal(new Rgba(119, 0, 125, 255), table.RangeFolded);
    }

    [Fact]
    public void ExactStopColors()
    {
        var table = Gr2Palette.Parse(SamplePal);
        var ramp = table.BuildRgba256();

        Rgba At(float value)
        {
            // Ceiling: sample at-or-above the stop, never a hair below it (bands are
            // half-open, so just-below belongs to the previous band by design).
            int i = Math.Clamp((int)MathF.Ceiling((value - table.MinValue) / table.Range * 255f), 0, 255);
            return new Rgba(ramp[i * 4], ramp[i * 4 + 1], ramp[i * 4 + 2], ramp[i * 4 + 3]);
        }

        // Stop values rarely land exactly on the 256-entry grid; ±4 per channel is
        // invisible in rendering and robust to that quantization.
        static void AssertClose(Rgba expected, Rgba actual)
        {
            Assert.InRange(actual.R, Math.Max(0, expected.R - 4), Math.Min(255, expected.R + 4));
            Assert.InRange(actual.G, Math.Max(0, expected.G - 4), Math.Min(255, expected.G + 4));
            Assert.InRange(actual.B, Math.Max(0, expected.B - 4), Math.Min(255, expected.B + 4));
            Assert.InRange(actual.A, Math.Max(0, expected.A - 4), Math.Min(255, expected.A + 4));
        }

        AssertClose(new Rgba(100, 100, 100, 255), At(0));
        AssertClose(new Rgba(0, 200, 0, 128), At(20));    // Color4 alpha honored
        AssertClose(new Rgba(255, 0, 0, 255), At(50));
        // Just under the top stop: the 50-band has blended nearly to the 65 start color.
        AssertClose(new Rgba(255, 0, 250, 255), At(64.7f));
        // The very top texel carries the top band's own end (second) color.
        AssertClose(new Rgba(128, 0, 128, 255), At(65));
    }

    [Fact]
    public void SolidBandDoesNotInterpolate()
    {
        var table = Gr2Palette.Parse(SamplePal);
        var ramp = table.BuildRgba256();
        // Between 40 and 50 the SolidColor band stays constant.
        int i45 = (int)MathF.Round((45 - table.MinValue) / table.Range * 255f);
        Assert.Equal(new Rgba(255, 200, 0, 255),
            new Rgba(ramp[i45 * 4], ramp[i45 * 4 + 1], ramp[i45 * 4 + 2], ramp[i45 * 4 + 3]));
    }

    [Fact]
    public void GradientBandInterpolatesTowardNextBand()
    {
        var table = Gr2Palette.Parse(SamplePal);
        var ramp = table.BuildRgba256();
        // Between 20 (0,200,0) and 40 (255,200,0) — a plain Color band blends to the next.
        int i30 = (int)MathF.Round((30 - table.MinValue) / table.Range * 255f);
        var color = new Rgba(ramp[i30 * 4], ramp[i30 * 4 + 1], ramp[i30 * 4 + 2], ramp[i30 * 4 + 3]);
        Assert.InRange(color.R, 100, 155); // ≈ halfway 0→255
        Assert.Equal(200, color.G);
    }

    [Fact]
    public void SecondColorGradientUsedWithinTopBand()
    {
        var table = Gr2Palette.Parse(SamplePal);
        // Top band 65: start (255,0,255) blending toward its own second color (128,0,128).
        // Values above the top stop clamp to the end color.
        var ramp = table.BuildRgba256();
        Assert.Equal(new Rgba(128, 0, 128, 255), new Rgba(ramp[255 * 4], ramp[255 * 4 + 1], ramp[255 * 4 + 2], ramp[255 * 4 + 3]));
    }

    [Fact]
    public void RejectsDegenerateTables()
    {
        Assert.Throws<FormatException>(() => Gr2Palette.Parse("Product: BR\nColor: 10 1 2 3\n"));
    }
}
