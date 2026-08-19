using System.Globalization;
using System.Text;
using OpenWSR.Geo;

namespace OpenWSR.Placefiles;

/// <summary>
/// Reads and writes drawings as GRLevelX placefiles.
///
/// The round trip is not quite lossless in the format itself, because a placefile has no
/// circle: a circle has to go out as a ring of points, and any reader that does not know
/// better sees a polygon. Rather than invent a second file format to avoid that, the writer
/// leaves a comment ahead of the ring naming the centre and radius. GR ignores it — a
/// placefile comment is a comment — and OpenWSR uses it to put the circle back together.
/// A drawing that has been through a tool that strips comments still opens; the circles
/// just come back as the rings they were drawn as, which is what they look like anyway.
/// </summary>
public static class DrawingFile
{
    /// <summary>
    /// Points around a circle. Sixty is one every six degrees, which is smooth at any zoom
    /// a radar is looked at and still a small file.
    /// </summary>
    public const int CircleSegments = 60;

    private const string CircleHint = "; OpenWSR-Circle:";

    /// <summary>A radius within this fraction still counts as the hinted circle on read.</summary>
    private const double CircleTolerance = 0.02;

    public static string Write(DrawingDocument document)
    {
        var text = new StringBuilder();
        text.AppendLine($"Title: {document.Title}");
        text.AppendLine("RefreshSeconds: 99999");
        text.AppendLine("; Drawn in OpenWSR. Editing this by hand is fine — it is an ordinary placefile.");
        text.AppendLine();

        foreach (var shape in document.Shapes)
        {
            var c = shape.Color;
            text.AppendLine(Invariant($"Color: {c.R} {c.G} {c.B} {c.A}"));

            switch (shape)
            {
                case DrawnText label:
                    // Font 1, and the text quoted so a comma or a semicolon inside it
                    // cannot be read as syntax.
                    text.AppendLine(Invariant(
                        $"Text: {label.LatDeg:F6}, {label.LonDeg:F6}, 1, \"{Escape(label.Text)}\""));
                    break;

                case DrawnCircle circle:
                    text.AppendLine(Invariant(
                        $"{CircleHint} {circle.LatDeg:F6}, {circle.LonDeg:F6}, {circle.RadiusM:F1}"));
                    WritePoints(text, shape, CirclePoints(circle), closed: true, filled: false);
                    break;

                case DrawnPath path:
                    WritePoints(text, shape, path.Points, path.Closed, path.Filled);
                    break;
            }
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>The ring a circle is drawn as: true ground radius, closed on its first point.</summary>
    public static IReadOnlyList<PlacePoint> CirclePoints(DrawnCircle circle)
    {
        var points = new List<PlacePoint>(CircleSegments + 1);
        for (int i = 0; i <= CircleSegments; i++)
        {
            double azimuth = 2 * Math.PI * (i % CircleSegments) / CircleSegments;
            var (lat, lon) = GeoMath.Offset(circle.LatDeg, circle.LonDeg, azimuth, circle.RadiusM);
            points.Add(new PlacePoint(lat, lon));
        }
        return points;
    }

    private static void WritePoints(
        StringBuilder text, DrawnShape shape,
        IReadOnlyList<PlacePoint> points, bool closed, bool filled)
    {
        if (points.Count < 2) return;

        // Polygon is the only filled primitive; an unfilled shape is a Line even when it
        // closes, because a Polygon with no fill is not a thing a placefile can say.
        text.AppendLine(filled ? "Polygon:" : Invariant($"Line: {shape.WidthPx:0.##}, 0"));
        foreach (var point in points)
            text.AppendLine(Invariant($" {point.LatDeg:F6}, {point.LonDeg:F6}"));

        // A closed shape repeats its first point; that is how a contour says it is closed.
        if (closed && points[0] != points[^1])
            text.AppendLine(Invariant($" {points[0].LatDeg:F6}, {points[0].LonDeg:F6}"));

        text.AppendLine("End:");
    }

    /// <summary>
    /// Read a drawing back. Any placefile works, not only one this wrote — importing
    /// someone else's warning polygons to trace over is a reasonable thing to want.
    /// </summary>
    public static DrawingDocument Read(string text)
    {
        var document = PlacefileParser.Parse(text);
        var hints = ReadCircleHints(text);
        var shapes = new List<DrawnShape>();

        foreach (var item in document.Items)
        {
            switch (item)
            {
                case PlacefileLabel label:
                    shapes.Add(new DrawnText(label.LatDeg, label.LonDeg, label.Text)
                    {
                        Color = label.Color,
                    });
                    break;

                case PlacefileLine line:
                {
                    // A ring that matches an unclaimed hint is the circle it was drawn as.
                    int hit = hints.FindIndex(h => Matches(h, line.Points));
                    if (hit >= 0)
                    {
                        var (lat, lon, radius) = hints[hit];
                        hints.RemoveAt(hit);
                        shapes.Add(new DrawnCircle(lat, lon, radius)
                        {
                            Color = line.Color,
                            WidthPx = line.WidthPx,
                        });
                        break;
                    }
                    shapes.Add(new DrawnPath(line.Points, Closed(line.Points))
                    {
                        Color = line.Color,
                        WidthPx = line.WidthPx,
                    });
                    break;
                }

                case PlacefilePolygon polygon:
                    // Each contour is its own shape: a placefile packs several into one
                    // statement, but they are separately drawn, moved and deleted here.
                    foreach (var contour in polygon.Contours)
                    {
                        if (contour.Count < 3) continue;
                        shapes.Add(new DrawnPath(contour, Closed: true, Filled: true)
                        {
                            Color = polygon.Color,
                        });
                    }
                    break;
            }
        }

        return new DrawingDocument(
            string.IsNullOrWhiteSpace(document.Title) ? "OpenWSR drawing" : document.Title!,
            shapes);
    }

    private static List<(double Lat, double Lon, double RadiusM)> ReadCircleHints(string text)
    {
        var hints = new List<(double, double, double)>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith(CircleHint, StringComparison.OrdinalIgnoreCase)) continue;

            var parts = line[CircleHint.Length..].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) &&
                radius > 0)
                hints.Add((lat, lon, radius));
        }
        return hints;
    }

    /// <summary>
    /// Does this run of points look like the hinted circle? Checked by geometry rather than
    /// by position in the file, so a hand-edited or reordered file still reassembles.
    /// </summary>
    private static bool Matches(
        (double Lat, double Lon, double RadiusM) hint, IReadOnlyList<PlacePoint> points)
    {
        if (points.Count < 8) return false;
        double slack = hint.RadiusM * CircleTolerance;
        foreach (var point in points)
        {
            double distance = GeoMath.DistanceM(hint.Lat, hint.Lon, point.LatDeg, point.LonDeg);
            if (Math.Abs(distance - hint.RadiusM) > slack) return false;
        }
        return true;
    }

    private static bool Closed(IReadOnlyList<PlacePoint> points) =>
        points.Count > 2 && points[0] == points[^1];

    /// <summary>A quote inside a label would end the string early.</summary>
    private static string Escape(string text) => text.Replace("\"", "'");

    private static string Invariant(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);
}
