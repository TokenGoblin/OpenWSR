using OpenWSR.Geo;
using OpenWSR.Placefiles;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Drawings are saved as placefiles, so the proof that the format works is that a drawing
/// survives a trip out through the writer and back in through the parser that everything
/// else here already depends on. No mocks: the real parser reads the real output.
/// </summary>
public sealed class DrawingFileTests
{
    private static DrawingDocument RoundTrip(DrawingDocument document) =>
        DrawingFile.Read(DrawingFile.Write(document));

    [Fact]
    public void APolylineComesBackWithItsPointsIntact()
    {
        var points = new[]
        {
            new PlacePoint(35.33, -97.28),
            new PlacePoint(35.41, -97.10),
            new PlacePoint(35.52, -96.95),
        };
        var original = new DrawingDocument("Squall line", [new DrawnPath(points)]);

        var shape = Assert.IsType<DrawnPath>(Assert.Single(RoundTrip(original).Shapes));
        Assert.Equal(3, shape.Points.Count);
        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(points[i].LatDeg, shape.Points[i].LatDeg, 5);
            Assert.Equal(points[i].LonDeg, shape.Points[i].LonDeg, 5);
        }
    }

    [Fact]
    public void TheTitleSurvives() =>
        Assert.Equal("Squall line", RoundTrip(
            new DrawingDocument("Squall line", [new DrawnPath([
                new PlacePoint(35, -97), new PlacePoint(36, -96)])])).Title);

    [Fact]
    public void AFilledPolygonComesBackFilledAndClosed()
    {
        var original = new DrawingDocument("Area", [new DrawnPath(
            [new PlacePoint(35, -97), new PlacePoint(36, -97), new PlacePoint(36, -96)],
            Closed: true, Filled: true)]);

        var shape = Assert.IsType<DrawnPath>(Assert.Single(RoundTrip(original).Shapes));
        Assert.True(shape.Filled);
        Assert.True(shape.Closed);
    }

    [Fact]
    public void AnUnfilledClosedShapeStaysUnfilled()
    {
        // A Polygon in a placefile is always filled, so an outline has to go out as a Line
        // that happens to close. It must not come back filled.
        var original = new DrawingDocument("Outline", [new DrawnPath(
            [new PlacePoint(35, -97), new PlacePoint(36, -97), new PlacePoint(36, -96)],
            Closed: true, Filled: false)]);

        var shape = Assert.IsType<DrawnPath>(Assert.Single(RoundTrip(original).Shapes));
        Assert.False(shape.Filled);
        Assert.True(shape.Closed);
    }

    [Fact]
    public void ColourAndWidthSurvive()
    {
        var original = new DrawingDocument("Coloured", [new DrawnPath(
            [new PlacePoint(35, -97), new PlacePoint(36, -96)])
        {
            Color = new PlaceColor(12, 240, 77, 180),
            WidthPx = 4.5f,
        }]);

        var shape = Assert.IsType<DrawnPath>(Assert.Single(RoundTrip(original).Shapes));
        Assert.Equal(new PlaceColor(12, 240, 77, 180), shape.Color);
        Assert.Equal(4.5f, shape.WidthPx, 2);
    }

    [Fact]
    public void ACircleComesBackAsACircleNotARingOfPoints()
    {
        var original = new DrawingDocument("Range ring",
            [new DrawnCircle(35.3331, -97.2778, 40_000)]);

        var circle = Assert.IsType<DrawnCircle>(Assert.Single(RoundTrip(original).Shapes));
        Assert.Equal(35.3331, circle.LatDeg, 4);
        Assert.Equal(-97.2778, circle.LonDeg, 4);
        Assert.Equal(40_000, circle.RadiusM, 1);
    }

    [Fact]
    public void TheRingACircleIsWrittenAsHasTheRadiusItClaims()
    {
        // The point of storing a circle as centre and radius: the ring must be a true
        // ground radius, not a Mercator one, or it is an ellipse everywhere but the equator.
        var circle = new DrawnCircle(61.16, -149.99, 75_000);   // Anchorage, where the
        var ring = DrawingFile.CirclePoints(circle);            // distortion is severe

        foreach (var point in ring)
        {
            double distance = GeoMath.DistanceM(
                circle.LatDeg, circle.LonDeg, point.LatDeg, point.LonDeg);
            Assert.InRange(distance, 74_990, 75_010);   // within 10 m
        }
    }

    [Fact]
    public void ACircleStrippedOfItsHintStillOpensAsAShape()
    {
        // Any tool that drops comments — and a user with an editor — leaves the ring
        // without its hint. It must still load, as the ring it visibly is.
        string text = DrawingFile.Write(new DrawingDocument("Ring",
            [new DrawnCircle(35.33, -97.28, 30_000)]));
        string stripped = string.Join('\n',
            text.Split('\n').Where(l => !l.TrimStart().StartsWith("; OpenWSR-Circle:")));

        var shape = Assert.Single(DrawingFile.Read(stripped).Shapes);
        var path = Assert.IsType<DrawnPath>(shape);
        Assert.True(path.Closed);
        Assert.Equal(DrawingFile.CircleSegments + 1, path.Points.Count);
    }

    [Fact]
    public void TwoCirclesOfTheSameRadiusDoNotStealEachOthersHint()
    {
        var original = new DrawingDocument("Two rings", [
            new DrawnCircle(35.33, -97.28, 25_000),
            new DrawnCircle(41.60, -93.61, 25_000),
        ]);

        var circles = RoundTrip(original).Shapes.OfType<DrawnCircle>().ToList();
        Assert.Equal(2, circles.Count);
        Assert.Contains(circles, c => Math.Abs(c.LatDeg - 35.33) < 0.01);
        Assert.Contains(circles, c => Math.Abs(c.LatDeg - 41.60) < 0.01);
    }

    [Fact]
    public void TextSurvivesIncludingTheCharactersThatAreSyntax()
    {
        // A comma would split the statement and a semicolon would start a comment, so the
        // label is quoted on the way out.
        var original = new DrawingDocument("Notes",
            [new DrawnText(35.33, -97.28, "Hook echo; inflow notch, 20:16Z")]);

        var label = Assert.IsType<DrawnText>(Assert.Single(RoundTrip(original).Shapes));
        Assert.Equal("Hook echo; inflow notch, 20:16Z", label.Text);
        Assert.Equal(35.33, label.LatDeg, 4);
    }

    [Fact]
    public void AQuoteInsideALabelDoesNotEndTheStringEarly()
    {
        var original = new DrawingDocument("Notes",
            [new DrawnText(35.33, -97.28, "the \"hook\" here")]);

        var label = Assert.IsType<DrawnText>(Assert.Single(RoundTrip(original).Shapes));
        Assert.DoesNotContain('"', label.Text);
        Assert.Contains("hook", label.Text);
    }

    [Fact]
    public void AMixedDrawingKeepsEveryShapeAndItsKind()
    {
        var original = new DrawingDocument("Everything", [
            new DrawnPath([new PlacePoint(35, -97), new PlacePoint(36, -96)]),
            new DrawnPath([new PlacePoint(35, -97), new PlacePoint(36, -97), new PlacePoint(36, -96)],
                Closed: true, Filled: true),
            new DrawnCircle(35.5, -96.5, 15_000),
            new DrawnText(35.2, -97.1, "here"),
        ]);

        var shapes = RoundTrip(original).Shapes;
        Assert.Equal(4, shapes.Count);
        Assert.Single(shapes.OfType<DrawnCircle>());
        Assert.Single(shapes.OfType<DrawnText>());
        Assert.Equal(2, shapes.OfType<DrawnPath>().Count());
    }

    [Fact]
    public void AForeignPlacefileImportsAsShapes()
    {
        // Tracing over someone else's polygons is a reasonable thing to want, so the reader
        // is not restricted to files this wrote.
        const string foreign = """
            Title: Someone else's warnings
            Color: 255 0 0
            Polygon:
             35.0, -97.0
             36.0, -97.0
             36.0, -96.0
             35.0, -97.0
            End:
            Text: 35.5, -96.5, 1, "TOR"
            """;

        var drawing = DrawingFile.Read(foreign);
        Assert.Equal("Someone else's warnings", drawing.Title);
        Assert.Single(drawing.Shapes.OfType<DrawnPath>());
        Assert.Single(drawing.Shapes.OfType<DrawnText>());
        Assert.Equal(new PlaceColor(255, 0, 0), drawing.Shapes[0].Color);
    }

    [Fact]
    public void AnEmptyDrawingWritesAFileThatReadsBackEmpty() =>
        Assert.Empty(DrawingFile.Read(DrawingFile.Write(DrawingDocument.Empty)).Shapes);

    [Fact]
    public void ASingleClickIsNotAShape()
    {
        // One point is not a line. It must not be written, or the file grows statements
        // with nothing in them that the parser then discards anyway.
        string text = DrawingFile.Write(new DrawingDocument("Stray",
            [new DrawnPath([new PlacePoint(35, -97)])]));

        Assert.Empty(DrawingFile.Read(text).Shapes);
    }
}
