using OpenWSR.Placefiles;

namespace OpenWSR.Nexrad.Tests;

public class PlacefileTests
{
    private const string Sample = """
        ; a comment line
        Title: Test Overlay
        RefreshSeconds: 90
        Threshold: 999
        Color: 255 0 0
        Place: 35.10, -97.20, Norman ; trailing comment
        Threshold: 150
        Color: 0 255 0 128
        Line: 3, 0, "Warning track"
        35.00,-97.00
        35.50,-97.50
        36.00,-98.00
        End:
        Polygon:
        34.0,-96.0
        34.0,-95.0
        35.0,-95.0
        34.0,-96.0
        End:
        Font: 1, 11, 1, "Courier New"
        Object: 41.27,-91.67
          Text: -17, 13, 1, " 81 ", "hover; with semicolon"
          Icon: 0, 0, 260, 1, 2
        End:
        Triangles:
        30,-90
        31,-90
        31,-91
        End:
        """;

    [Fact]
    public void ParsesHeaderAndSimpleStatements()
    {
        var doc = PlacefileParser.Parse(Sample);
        Assert.Equal("Test Overlay", doc.Title);
        Assert.Equal(TimeSpan.FromSeconds(90), doc.Refresh);
        Assert.Single(doc.Fonts);
        Assert.Equal("Courier New", doc.Fonts[0].Face);
        Assert.Contains("triangles", doc.UnsupportedStatements);
    }

    [Fact]
    public void PlaceCarriesColourAndThreshold()
    {
        var doc = PlacefileParser.Parse(Sample);
        var place = doc.Items.OfType<PlacefileLabel>().First(l => l.Text == "Norman");
        Assert.Equal(35.10, place.LatDeg, 5);
        Assert.Equal(-97.20, place.LonDeg, 5);
        Assert.Equal(new PlaceColor(255, 0, 0), place.Color);
        Assert.Equal(999, place.ThresholdNm);
    }

    [Fact]
    public void LineKeepsWidthHoverAndPoints()
    {
        var doc = PlacefileParser.Parse(Sample);
        var line = doc.Items.OfType<PlacefileLine>().Single();
        Assert.Equal(3f, line.WidthPx);
        Assert.Equal("Warning track", line.Hover);
        Assert.Equal(3, line.Points.Count);
        Assert.Equal(150, line.ThresholdNm);
        Assert.Equal(new PlaceColor(0, 255, 0, 128), line.Color);
    }

    [Fact]
    public void PolygonClosesOnTheRepeatedFirstPoint()
    {
        var doc = PlacefileParser.Parse(Sample);
        var polygon = doc.Items.OfType<PlacefilePolygon>().Single();
        Assert.Single(polygon.Contours);
        Assert.Equal(3, polygon.Contours[0].Count); // closing repeat is not stored
    }

    [Fact]
    public void ObjectBlockTurnsCoordinatesIntoPixelOffsets()
    {
        var doc = PlacefileParser.Parse(Sample);
        var text = doc.Items.OfType<PlacefileLabel>().First(l => l.Text.Trim() == "81");
        Assert.Equal(41.27, text.LatDeg, 5);   // anchored at the object centre
        Assert.Equal(-91.67, text.LonDeg, 5);
        Assert.Equal(-17, text.OffsetXPx, 5);  // and offset in pixels
        Assert.Equal(13, text.OffsetYPx, 5);
        Assert.Equal("hover; with semicolon", text.Hover); // semicolons survive inside quotes

        var icon = doc.Items.OfType<PlacefileIcon>().Single();
        Assert.Equal(41.27, icon.LatDeg, 5);
        Assert.Equal(260, icon.AngleDeg);
        Assert.Equal(2, icon.IconNumber);
    }

    [Fact]
    public void ParsesRealIemAsosPlacefile()
    {
        var doc = PlacefileParser.Parse(File.ReadAllText(TestData.Path("placefiles/iem_asos.txt")));
        Assert.Equal("Iowa ASOS Only", doc.Title);
        Assert.Equal(TimeSpan.FromMinutes(5), doc.Refresh);
        Assert.NotEmpty(doc.Items.OfType<PlacefileIcon>());

        // Every station is an Object block, so its labels are anchored and offset.
        var anchored = doc.Items.OfType<PlacefileLabel>().Where(l => l.Anchor is not null).ToList();
        Assert.NotEmpty(anchored);
        Assert.All(anchored, l => Assert.InRange(l.LatDeg, 40, 44));   // Iowa
        Assert.All(anchored, l => Assert.InRange(l.LonDeg, -97, -90));
    }

    [Fact]
    public void ParsesRealIemWarningTrackPlacefile()
    {
        var doc = PlacefileParser.Parse(TestData.Path("placefiles/iem_time_mot_loc.txt") is var path
            ? File.ReadAllText(path) : "");
        Assert.Contains("Warning Time-Mot-Loc", doc.Title);
        Assert.Equal(TimeSpan.FromSeconds(60), doc.Refresh);

        var lines = doc.Items.OfType<PlacefileLine>().ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.True(l.Points.Count >= 2));

        // Time labels along each track, e.g. "2028z".
        var labels = doc.Items.OfType<PlacefileLabel>().ToList();
        Assert.NotEmpty(labels);
        Assert.Contains(labels, l => l.Text.EndsWith('z'));
    }
}
