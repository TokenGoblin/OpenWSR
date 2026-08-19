using System.IO;
using OpenWSR.App;
using OpenWSR.Placefiles;

namespace OpenWSR.App.Tests;

/// <summary>
/// Icon sheets: the declaration, and where the image is actually fetched from. A sheet
/// name is usually relative to the placefile that named it, so resolving it wrongly means
/// every icon silently falls back to a plain marker.
/// </summary>
public class PlacefileIconTests
{
    [Fact]
    public void IconFileDeclarationIsCaptured()
    {
        var document = PlacefileParser.Parse("""
            Title: Icons
            IconFile: 1, 15, 25, 8, 25, "hail.png"
            Icon: 35.3, -97.5, 0, 1, 3, "big hail"
            """);

        var sheet = Assert.Single(document.IconSheets);
        Assert.Equal(1, sheet.Number);
        Assert.Equal(15, sheet.WidthPx);
        Assert.Equal(25, sheet.HeightPx);
        Assert.Equal(8, sheet.HotXPx);
        Assert.Equal(25, sheet.HotYPx);   // hot spot at the bottom: a pin standing on the point
        Assert.Equal("hail.png", sheet.Source);

        var icon = Assert.IsType<PlacefileIcon>(Assert.Single(document.Items));
        Assert.Equal(1, icon.FileNumber);
        Assert.Equal(3, icon.IconNumber);
    }

    [Fact]
    public void IconSheetsAreNoLongerReportedUnsupported()
    {
        // They used to be listed as "icon images" in the unsupported set, which is what the
        // placefile panel shows in its tooltip.
        var document = PlacefileParser.Parse("""
            IconFile: 1, 16, 16, 8, 8, "sheet.png"
            """);
        Assert.DoesNotContain("icon images", document.UnsupportedStatements);
        Assert.Single(document.IconSheets);
    }

    [Fact]
    public void SeveralSheetsCoexistWithTheirOwnNumbers()
    {
        var document = PlacefileParser.Parse("""
            IconFile: 1, 16, 16, 8, 8, "a.png"
            IconFile: 2, 32, 32, 16, 32, "b.png"
            """);
        Assert.Equal(2, document.IconSheets.Count);
        Assert.Equal("a.png", document.IconSheets.Single(s => s.Number == 1).Source);
        Assert.Equal(32, document.IconSheets.Single(s => s.Number == 2).HeightPx);
    }

    [Theory]
    [InlineData("A sheet with no dimensions", "IconFile: 1, 0, 0, 0, 0, \"x.png\"")]
    [InlineData("Too few fields", "IconFile: 1, 16, 16, \"x.png\"")]
    [InlineData("Not numbers", "IconFile: a, b, c, d, e, \"x.png\"")]
    public void MalformedDeclarationsAreIgnoredRatherThanCrashing(string _, string line) =>
        Assert.Empty(PlacefileParser.Parse(line).IconSheets);

    [Fact]
    public void SheetNamesResolveRelativeToTheirPlacefile()
    {
        Assert.Equal(
            "https://example.com/wx/icons/hail.png",
            PlacefileController.ResolveSheetUrl("https://example.com/wx/feed.php", "icons/hail.png"));

        // A sibling of the placefile.
        Assert.Equal(
            "https://example.com/wx/hail.png",
            PlacefileController.ResolveSheetUrl("https://example.com/wx/feed.php", "hail.png"));

        // Up a level.
        Assert.Equal(
            "https://example.com/hail.png",
            PlacefileController.ResolveSheetUrl("https://example.com/wx/feed.php", "../hail.png"));
    }

    [Fact]
    public void AnAbsoluteSheetUrlIsTakenAsGiven()
    {
        Assert.Equal(
            "https://cdn.example.net/sheet.png",
            PlacefileController.ResolveSheetUrl(
                "https://example.com/wx/feed.php", "https://cdn.example.net/sheet.png"));
    }

    [Fact]
    public void ALocalPlacefileResolvesSheetsBesideItself()
    {
        string resolved = PlacefileController.ResolveSheetUrl(
            Path.Combine("C:", "wx", "overlay.txt"), "hail.png");
        Assert.EndsWith("hail.png", resolved);
        Assert.Contains("wx", resolved);
    }

    /// <summary>
    /// Build a tiny PNG in memory. Written by hand rather than checked in as a fixture so
    /// the alpha channel is unambiguous — the whole point of the test is which channels the
    /// source actually carries.
    /// </summary>
    private static byte[] MakePng(System.Windows.Media.PixelFormat format, byte[] pixels, int stride)
    {
        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            2, 2, 96, 96, format, null, pixels, stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void BlackBecomesTransparentInASheetWithNoAlphaChannel()
    {
        // White, black, black, white — as an alpha-less sheet, the black is the ground.
        byte[] rgb = [255, 255, 255,  0, 0, 0,
                        0,   0,   0,  255, 255, 255];
        var sheet = PlacefileController.DecodeSheet(
            MakePng(System.Windows.Media.PixelFormats.Rgb24, rgb, 6), 2, 2);

        Assert.NotNull(sheet);
        Assert.Equal(255, sheet!.Bgra[3]);    // white pixel stays
        Assert.Equal(0, sheet.Bgra[7]);       // black pixel is keyed out
        Assert.Equal(0, sheet.Bgra[11]);
        Assert.Equal(255, sheet.Bgra[15]);
    }

    [Fact]
    public void RealAlphaIsTrustedSoBlackArtworkSurvives()
    {
        // A black icon that declares itself opaque must not be punched through: the key
        // only applies when there is no alpha to go on.
        byte[] bgra = [0, 0, 0, 255,   0, 0, 0, 0,
                       0, 0, 0, 255,   0, 0, 0, 0];
        var sheet = PlacefileController.DecodeSheet(
            MakePng(System.Windows.Media.PixelFormats.Bgra32, bgra, 8), 2, 2);

        Assert.NotNull(sheet);
        Assert.Equal(255, sheet!.Bgra[3]);
        Assert.Equal(0, sheet.Bgra[7]);
        Assert.Equal(255, sheet.Bgra[11]);
    }
}
