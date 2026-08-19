using OpenWSR.Geo;
using OpenWSR.Placefiles;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// The rules the drawing tool has to obey, expressed against the file format rather than
/// the controller — the controller needs a render device, and every decision worth pinning
/// (what closes, what fills, what is too small to be a shape) shows up in what gets written.
/// </summary>
public sealed class DrawingBehaviourTests
{
    private static readonly PlacePoint[] Three =
    [
        new(35.33, -97.28), new(35.41, -97.10), new(35.52, -96.95),
    ];

    [Fact]
    public void AnAreaClosesBackOnItsFirstPointEvenIfTheUserDidNot()
    {
        // The user clicks three corners and presses Enter; they never click the first
        // corner again, so the file has to repeat it. Asserted against the text rather
        // than the parsed contour because the repeat is how a contour says it has ended —
        // the parser consumes it as the terminator and does not hand it back.
        string text = DrawingFile.Write(new DrawingDocument("Area",
            [new DrawnPath(Three, Closed: true, Filled: true)]));

        var coordinates = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("35."))
            .ToList();

        Assert.Equal(4, coordinates.Count);
        Assert.Equal(coordinates[0], coordinates[^1]);

        // And it still parses as one closed contour of the three corners drawn.
        var contour = Assert.Single(
            Assert.IsType<PlacefilePolygon>(Assert.Single(PlacefileParser.Parse(text).Items)).Contours);
        Assert.Equal(3, contour.Count);
    }

    [Fact]
    public void ALineIsLeftOpen()
    {
        string text = DrawingFile.Write(new DrawingDocument("Line", [new DrawnPath(Three)]));

        var line = Assert.IsType<PlacefileLine>(Assert.Single(PlacefileParser.Parse(text).Items));
        Assert.Equal(3, line.Points.Count);
        Assert.NotEqual(line.Points[0], line.Points[^1]);
    }

    [Fact]
    public void AnAreaIsWrittenAsAPolygonAndALineIsNot()
    {
        // These are different statements in the format, and only Polygon fills.
        var filled = PlacefileParser.Parse(DrawingFile.Write(new DrawingDocument("a",
            [new DrawnPath(Three, Closed: true, Filled: true)])));
        var stroked = PlacefileParser.Parse(DrawingFile.Write(new DrawingDocument("b",
            [new DrawnPath(Three, Closed: true)])));

        Assert.IsType<PlacefilePolygon>(Assert.Single(filled.Items));
        Assert.IsType<PlacefileLine>(Assert.Single(stroked.Items));
    }

    [Fact]
    public void ThicknessReachesTheFile()
    {
        string text = DrawingFile.Write(new DrawingDocument("Thick",
            [new DrawnPath(Three) { WidthPx = 6f }]));

        Assert.Equal(6f, Assert.IsType<PlacefileLine>(
            Assert.Single(PlacefileParser.Parse(text).Items)).WidthPx, 2);
    }

    [Fact]
    public void ACircleDrawnFromCentreToEdgeHasTheRadiusThatWasClicked()
    {
        // How the tool builds one: first click is the centre, second is a point on the
        // edge, and the radius is the geodesic distance between them.
        var (centreLat, centreLon) = (35.3331, -97.2778);
        var (edgeLat, edgeLon) = (35.6, -97.0);
        double radius = GeoMath.DistanceM(centreLat, centreLon, edgeLat, edgeLon);

        var circle = new DrawnCircle(centreLat, centreLon, radius);
        var ring = DrawingFile.CirclePoints(circle);

        // Measured to the ring's edges, not its corners. A sixty-sided polygon puts its
        // vertices two kilometres apart at this radius, so a nearest-vertex test would
        // pass on a ring that missed the clicked point entirely.
        double nearest = double.MaxValue;
        for (int i = 1; i < ring.Count; i++)
            nearest = Math.Min(nearest, GeoMath.DistanceToSegmentM(
                edgeLat, edgeLon,
                (ring[i - 1].LatDeg, ring[i - 1].LonDeg),
                (ring[i].LatDeg, ring[i].LonDeg)));

        // The bound is the sagitta of a six-degree arc, R(1 - cos 3 degrees), about 0.14 %
        // of the radius. Anything larger would mean the ring is not the circle asked for.
        Assert.True(nearest < radius * 0.005,
            $"clicked edge point is {nearest:F0} m from the drawn ring of radius {radius:F0} m");
    }

    [Fact]
    public void ACircleRingClosesOnItself()
    {
        var ring = DrawingFile.CirclePoints(new DrawnCircle(35.33, -97.28, 20_000));

        Assert.Equal(DrawingFile.CircleSegments + 1, ring.Count);
        Assert.Equal(ring[0].LatDeg, ring[^1].LatDeg, 9);
        Assert.Equal(ring[0].LonDeg, ring[^1].LonDeg, 9);
    }

    [Fact]
    public void ADrawingSurvivesBeingSavedAndReopenedRepeatedly()
    {
        // Save, reopen, save again: a format that loses something loses it on the second
        // pass even when the first looks right.
        var original = new DrawingDocument("Session", [
            new DrawnPath(Three) { Color = new PlaceColor(80, 220, 255), WidthPx = 3f },
            new DrawnPath(Three, Closed: true, Filled: true),
            new DrawnCircle(35.4, -97.2, 18_000),
            new DrawnText(35.35, -97.25, "couplet"),
        ]);

        var once = DrawingFile.Read(DrawingFile.Write(original));
        var twice = DrawingFile.Read(DrawingFile.Write(once));

        Assert.Equal(once.Shapes.Count, twice.Shapes.Count);
        Assert.Equal(
            once.Shapes.Select(s => s.GetType().Name),
            twice.Shapes.Select(s => s.GetType().Name));
        Assert.Equal(
            Assert.IsType<DrawnCircle>(once.Shapes[2]).RadiusM,
            Assert.IsType<DrawnCircle>(twice.Shapes[2]).RadiusM, 1);
    }

    [Fact]
    public void ADrawingIsGeographicSoItDoesNotMoveWithTheMap()
    {
        // Stored as degrees, never as pixels or Mercator metres — the whole point is that
        // an annotation stays on the storm when the view changes.
        var text = DrawingFile.Write(new DrawingDocument("Fixed",
            [new DrawnPath([new PlacePoint(35.3331, -97.2778), new PlacePoint(35.5, -97.0)])]));

        var line = Assert.IsType<PlacefileLine>(Assert.Single(PlacefileParser.Parse(text).Items));
        Assert.Equal(35.3331, line.Points[0].LatDeg, 4);
        Assert.Equal(-97.2778, line.Points[0].LonDeg, 4);
    }

    [Fact]
    public void AFileWrittenHereParsesWithNoUnsupportedStatements()
    {
        // Anything the parser reports as unsupported would be something GR might also
        // choke on, so the writer must only emit statements that are fully understood.
        var document = PlacefileParser.Parse(DrawingFile.Write(new DrawingDocument("All", [
            new DrawnPath(Three),
            new DrawnPath(Three, Closed: true, Filled: true),
            new DrawnCircle(35.4, -97.2, 12_000),
            new DrawnText(35.3, -97.3, "note"),
        ])));

        Assert.Empty(document.UnsupportedStatements);
        Assert.Equal(4, document.Items.Count);
    }

    [Fact]
    public void TheFileNamesItselfAsADrawingSoItIsRecognisableLater()
    {
        string text = DrawingFile.Write(new DrawingDocument("Session", [new DrawnPath(Three)]));

        Assert.Contains("Title: Session", text);
        Assert.Contains("OpenWSR", text);
    }
}
