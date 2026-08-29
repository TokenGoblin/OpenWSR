using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenWSR.Render.Tiles;

namespace OpenWSR.App.Tests;

/// <summary>
/// The dark basemap is derived rather than fetched, so the thing to assert is that it lands
/// where the style it replaces did.
/// </summary>
/// <remarks>
/// CARTO's "Dark Matter" began requiring an API key in August 2026, and the reference numbers
/// here were measured from its tiles before that: they cannot be re-fetched, which is exactly
/// why they are written down. The project's rule is to check against an independent
/// implementation rather than against our own output, and a tile drawn by someone else's
/// renderer is that.
/// </remarks>
public class TileToningTests
{
    /// <summary>
    /// Mean luminance of CARTO <c>dark_nolabels</c> z9/95/191 — northern Utah, the view in the
    /// 0.1.0 release screenshot — measured 2026-08-27 from a tile cached before the watermark.
    /// </summary>
    private const double CartoDarkMeanLuminance = 20.45;

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenWSR.slnx")))
            dir = dir.Parent!;
        if (dir is null) throw new InvalidOperationException("Repo root not found.");
        return Path.Combine(dir.FullName, "assets", "testdata", "tiles", "osm-z9-x95-y191.png");
    }

    private static byte[] FixtureBgra()
    {
        using var stream = File.OpenRead(FixturePath());
        var frame = BitmapDecoder.Create(
            stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        BitmapSource source = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }

    private static double MeanLuminance(byte[] bgra)
    {
        long total = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
            total += (29 * bgra[i] + 150 * bgra[i + 1] + 77 * bgra[i + 2]) >> 8;
        return total / (double)(bgra.Length / 4);
    }

    /// <summary>
    /// The property the whole transform exists for: the derived map is as dark as the one it
    /// replaces. The tolerance is wide on purpose. Two renderers drawing the same ground do
    /// not agree on how much landcover to paint, and CARTO's own per-tile mean ranges from 9.1
    /// to 20.5 across the twelve z9 tiles around this one — so a tight bound here would be
    /// asserting a coincidence. What pins the curve is
    /// <see cref="NamedOsmColoursLandWhereMeasured"/>; this asserts it is in the right country.
    /// </summary>
    [Fact]
    public void DerivedDarkMapMatchesTheStyleItReplaces()
    {
        var pixels = FixtureBgra();
        // Tolerance overloads throughout: Assert.Equal(double, double, int) is *precision* in
        // decimal places, which is a far tighter claim than anything measured here supports.
        Assert.Equal(219.4, MeanLuminance(pixels), tolerance: 0.1); // as published: a light map

        TileToning.Apply(pixels, TileTone.InvertedDark);
        Assert.Equal(CartoDarkMeanLuminance, MeanLuminance(pixels), tolerance: 3.0);
    }

    /// <summary>
    /// Where the named OSM colours land. These pin the curve so that changing it is a decision
    /// rather than a drift; the two that matter are the ground going black and the label text
    /// staying bright enough to read over an echo.
    /// </summary>
    [Theory]
    [InlineData(242, 239, 233, 6)]    // land       #f2efe9 -> near-black ground
    [InlineData(170, 211, 223, 32)]   // water      #aad3df
    [InlineData(200, 250, 204, 12)]   // park       #c8facc
    [InlineData(249, 178, 156, 35)]   // motorway   #f9b29c
    [InlineData(51, 51, 51, 189)]     // label text #333333 -> bright
    [InlineData(255, 255, 255, 0)]    // minor road #ffffff -> lost, see TileToning remarks
    public void NamedOsmColoursLandWhereMeasured(byte r, byte g, byte b, byte expected)
    {
        byte luminance = (byte)((29 * b + 150 * g + 77 * r) >> 8);
        Assert.Equal(expected, TileToning.Map(TileTone.InvertedDark, luminance));
    }

    /// <summary>
    /// Inverting is the point — a curve that merely darkened would leave OSM's near-white
    /// ground the brightest thing on screen, which is the job reflectivity has.
    /// </summary>
    [Fact]
    public void CurveInvertsAndNeverDoublesBack()
    {
        Assert.Equal(255, TileToning.Map(TileTone.InvertedDark, 0));
        Assert.Equal(0, TileToning.Map(TileTone.InvertedDark, 255));

        for (int i = 1; i < 256; i++)
        {
            Assert.True(
                TileToning.Map(TileTone.InvertedDark, (byte)i)
                    <= TileToning.Map(TileTone.InvertedDark, (byte)(i - 1)),
                $"luminance {i} is brighter than {i - 1} after toning");
        }
    }

    /// <summary>
    /// Desaturating is half the transform: an inverted colour map turns OSM's green landcover
    /// magenta and its blue water orange, and then the palette has company.
    /// </summary>
    [Fact]
    public void TonedPixelsAreGreyAndKeepTheirAlpha()
    {
        byte[] pixels = [170, 211, 223, 128, 200, 250, 204, 255];
        TileToning.Apply(pixels, TileTone.InvertedDark);

        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
        Assert.Equal(pixels[4], pixels[5]);
        Assert.Equal(pixels[5], pixels[6]);
        Assert.Equal(128, pixels[3]);
        Assert.Equal(255, pixels[7]);
    }

    [Fact]
    public void AsPublishedLeavesEveryByteAlone()
    {
        byte[] pixels = [170, 211, 223, 128, 200, 250, 204, 255];
        byte[] before = [.. pixels];

        TileToning.Apply(pixels, TileTone.AsPublished);

        Assert.Equal(before, pixels);
    }

    /// <summary>
    /// The dark style is the light one toned on the way to the texture, not a second source.
    /// Sharing the name is what makes the two share a disk cache and an attribution line, so a
    /// user switching between them re-downloads nothing.
    /// </summary>
    [Fact]
    public void DarkOsmIsTheSameTilesAsLightOsm()
    {
        var light = TileProvider.Osm("OpenWSR/0.1");
        var dark = TileProvider.OsmDark("OpenWSR/0.1");

        Assert.Equal(light.Name, dark.Name);
        Assert.Equal(light.UrlTemplate, dark.UrlTemplate);
        Assert.Equal(TileTone.AsPublished, light.Tone);
        Assert.Equal(TileTone.InvertedDark, dark.Tone);
    }
}
