namespace OpenWSR.Placefiles;

/// <summary>
/// A shape the user drew on the map.
///
/// Drawings are stored as placefiles rather than in a format of their own. That is not a
/// shortcut: a placefile is already the interchange format everyone in this corner of the
/// world reads, so a saved drawing opens in GR, can be served to someone else, and comes
/// back into OpenWSR through the parser that is already golden-tested. One format, one
/// parser, no second thing to keep in step.
/// </summary>
public abstract record DrawnShape
{
    /// <summary>Amber by default — it reads on both the light basemap and heavy reflectivity.</summary>
    public PlaceColor Color { get; init; } = new(255, 200, 40);

    public float WidthPx { get; init; } = 2f;
}

/// <summary>
/// A run of points: a freehand stroke, a straight polyline, or a polygon. Whether it closes
/// and whether it fills are the only things that separate them, so they are one type rather
/// than three that would each need the same editing, hit-testing and serialisation.
/// </summary>
public sealed record DrawnPath(IReadOnlyList<PlacePoint> Points, bool Closed = false, bool Filled = false)
    : DrawnShape;

/// <summary>
/// A circle of true ground radius, kept as a centre and a distance rather than as the ring
/// it is drawn with, so that moving or resizing it stays exact and the radius can be
/// reported to the user.
/// </summary>
public sealed record DrawnCircle(double LatDeg, double LonDeg, double RadiusM) : DrawnShape;

/// <summary>A label pinned to a point.</summary>
public sealed record DrawnText(double LatDeg, double LonDeg, string Text) : DrawnShape;

/// <summary>A named set of shapes — what a save file holds.</summary>
public sealed record DrawingDocument(string Title, IReadOnlyList<DrawnShape> Shapes)
{
    public static DrawingDocument Empty { get; } = new("OpenWSR drawing", []);
}
