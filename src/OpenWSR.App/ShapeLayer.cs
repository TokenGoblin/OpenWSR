using OpenWSR.Geo;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// A set of shapes projected once and then drawn at whatever zoom asks for them.
/// </summary>
/// <remarks>
/// Vector geometry arrives at the resolution its finest use needs, and a radar view spans
/// four orders of magnitude of scale, so the same data is two hundred points per pixel at
/// one zoom and correct at another. Every layer built from a shapefile or a GeoJSON has that
/// problem identically, so the answer lives here once: project and bound at load, then cull
/// to the viewport and thin to a screen tolerance on the way out.
///
/// <para>Projection is the expensive half and does not depend on the camera, which is why it
/// is not repeated per view change.</para>
/// </remarks>
public sealed class ShapeLayer
{
    /// <summary>
    /// How far a thinned line may sit from the true one, in screen pixels. Under about a
    /// pixel there is nothing to see; over about two, the corner of a county visibly moves.
    /// </summary>
    public const double DefaultTolerancePx = 1.2;

    private sealed record Part(
        ShapeKind Kind,
        (double X, double Y)[] Points,
        double MinX, double MinY, double MaxX, double MaxY);

    private readonly List<Part> _parts;

    private ShapeLayer(List<Part> parts)
    {
        _parts = parts;
        PointCount = parts.Sum(p => p.Points.Length);
    }

    public int PartCount => _parts.Count;
    public int PointCount { get; }

    public static ShapeLayer From(IEnumerable<ShapeFeature> features)
    {
        var parts = new List<Part>();
        foreach (var feature in features)
            foreach (var run in feature.Parts)
            {
                if (run.Count == 0) continue;
                var points = new (double X, double Y)[run.Count];
                for (int i = 0; i < run.Count; i++)
                    points[i] = GeoMath.ToMercator(run[i].LatDeg, run[i].LonDeg);
                var (minX, minY, maxX, maxY) = Polyline.Bounds(points);
                parts.Add(new Part(feature.Kind, points, minX, minY, maxX, maxY));
            }
        return new ShapeLayer(parts);
    }

    /// <summary>
    /// Add everything inside <paramref name="cam"/>'s view to <paramref name="geometry"/>,
    /// thinned for the zoom it is at.
    /// </summary>
    public void Append(
        OverlayGeometry geometry, CameraSnapshot cam, uint colour, float widthPx,
        double tolerancePx = DefaultTolerancePx)
    {
        // A margin, so a small pan does not immediately expose an unbuilt edge.
        double halfW = cam.ViewportWidth * cam.MetersPerPixel * 0.75;
        double halfH = cam.ViewportHeight * cam.MetersPerPixel * 0.75;
        double minX = cam.CenterX - halfW, maxX = cam.CenterX + halfW;
        double minY = cam.CenterY - halfH, maxY = cam.CenterY + halfH;
        double tolerance = tolerancePx * cam.MetersPerPixel;

        foreach (var part in _parts)
        {
            if (part.MaxX < minX || part.MinX > maxX ||
                part.MaxY < minY || part.MinY > maxY) continue;

            if (part.Kind is ShapeKind.Point or ShapeKind.MultiPoint)
            {
                // A marker is a screen-sized symbol, not a ground extent.
                double arm = 4 * cam.MetersPerPixel;
                foreach (var (x, y) in part.Points)
                {
                    geometry.Lines.Add(new OverlayLine(
                        x - arm, y, x + arm, y, colour, widthPx, LineCaps.Both));
                    geometry.Lines.Add(new OverlayLine(
                        x, y - arm, x, y + arm, colour, widthPx, LineCaps.Both));
                }
                continue;
            }

            var thinned = Polyline.Simplify(part.Points, tolerance);
            for (int i = 0; i + 1 < thinned.Count; i++)
                geometry.Lines.Add((
                    thinned[i].X, thinned[i].Y,
                    thinned[i + 1].X, thinned[i + 1].Y, colour, widthPx));
        }
    }
}
