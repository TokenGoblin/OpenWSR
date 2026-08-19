namespace OpenWSR.Placefiles;

public readonly record struct PlaceColor(byte R, byte G, byte B, byte A = 255);

/// <summary>A point in a placefile: geographic, or a pixel offset from an Object's centre.</summary>
public readonly record struct PlacePoint(double LatDeg, double LonDeg)
{
    /// <summary>Inside an Object block these are screen pixels, not degrees.</summary>
    public double OffsetX => LatDeg;
    public double OffsetY => LonDeg;
}

public abstract record PlacefileItem
{
    public PlaceColor Color { get; init; } = new(255, 255, 255);
    /// <summary>Hide the item once the view is wider than this, in nautical miles.</summary>
    public double ThresholdNm { get; init; } = 999;
    public DateTimeOffset? VisibleFrom { get; init; }
    public DateTimeOffset? VisibleTo { get; init; }
    /// <summary>Object centre when the item came from an Object block, else null.</summary>
    public (double LatDeg, double LonDeg)? Anchor { get; init; }

    public bool VisibleAt(DateTimeOffset when) =>
        (VisibleFrom is null || when >= VisibleFrom) && (VisibleTo is null || when < VisibleTo);
}

/// <summary>Place or Text: a label, optionally offset from an Object centre in pixels.</summary>
public sealed record PlacefileLabel(
    double LatDeg, double LonDeg, string Text, int FontNumber = 0,
    double OffsetXPx = 0, double OffsetYPx = 0, string? Hover = null) : PlacefileItem;

public sealed record PlacefileLine(
    IReadOnlyList<PlacePoint> Points, float WidthPx, string? Hover = null) : PlacefileItem;

/// <summary>Polygon contours; a contour closes when its first point repeats.</summary>
public sealed record PlacefilePolygon(
    IReadOnlyList<IReadOnlyList<PlacePoint>> Contours) : PlacefileItem;

public sealed record PlacefileIcon(
    double LatDeg, double LonDeg, double AngleDeg, int FileNumber, int IconNumber,
    double OffsetXPx = 0, double OffsetYPx = 0, string? Hover = null) : PlacefileItem;

public sealed record PlacefileFont(int Number, int Pixels, int Flags, string Face);

/// <summary>
/// An <c>IconFile</c> declaration: one image holding a grid of icons, and the geometry
/// needed to cut it up. Cells are numbered from one, left to right then top to bottom.
///
/// The hot spot is the pixel inside the cell that sits on the coordinate — the tip of a
/// pin rather than its middle. Half the width and height means centred, which is what most
/// community files use.
/// </summary>
public sealed record PlacefileIconSheet(
    int Number, int WidthPx, int HeightPx, int HotXPx, int HotYPx, string Source);

public sealed record PlacefileDocument(
    string? Title,
    TimeSpan? Refresh,
    IReadOnlyList<PlacefileItem> Items,
    IReadOnlyList<PlacefileFont> Fonts,
    IReadOnlyList<PlacefileIconSheet> IconSheets,
    IReadOnlyList<string> UnsupportedStatements);
