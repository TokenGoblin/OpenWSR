using System.IO;
using OpenWSR.Geo;
using OpenWSR.Placefiles;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>What the next clicks will draw.</summary>
public enum DrawingKind
{
    /// <summary>Click each vertex; Enter finishes.</summary>
    Line,
    /// <summary>As a line, but closed and filled when it finishes.</summary>
    Polygon,
    /// <summary>Click the centre, then a point on the edge.</summary>
    Circle,
    /// <summary>Click where the label goes.</summary>
    Text,
}

/// <summary>
/// Annotating the map: lines, polygons, circles and labels the user places by hand.
///
/// Vertices are placed by clicking rather than dragging, because a left drag already pans
/// the map and taking that away for one tool would make the map feel broken while the tool
/// happened to be selected. Enter finishes a shape, Escape abandons it, Backspace takes
/// back the last point — the same keys any drawing program uses for the same jobs.
///
/// Everything is held in geographic coordinates, so a drawing stays where it was put when
/// the map is panned, zoomed, or moved to a different radar.
/// </summary>
public sealed class DrawingController(MapView mapView)
{
    private readonly List<DrawnShape> _shapes = [];
    private readonly List<PlacePoint> _pending = [];

    /// <summary>Rebuilt whenever anything changes; merged into the map overlay by the shell.</summary>
    public OverlayGeometry? Geometry { get; private set; }

    public IReadOnlyList<MapView.MapLabel> Labels { get; private set; } = [];

    public event Action? Changed;

    /// <summary>What to tell the user they can do next — the tool is modal, so it must say so.</summary>
    public event Action<string>? HintChanged;

    public DrawingKind Kind { get; set; } = DrawingKind.Line;
    public PlaceColor Color { get; set; } = new(255, 200, 40);
    public float WidthPx { get; set; } = 2f;

    /// <summary>The label the next Text click will place.</summary>
    public string PendingText { get; set; } = "";

    public int ShapeCount => _shapes.Count;
    public bool IsDrawing => _pending.Count > 0;

    /// <summary>True when there is anything worth saving or clearing.</summary>
    public bool HasContent => _shapes.Count > 0;

    public void OnClick(int xPx, int yPx)
    {
        var (lat, lon) = mapView.ScreenToLatLon(xPx, yPx);

        switch (Kind)
        {
            case DrawingKind.Text:
                if (string.IsNullOrWhiteSpace(PendingText))
                {
                    HintChanged?.Invoke("Type the label first, then click where it goes.");
                    return;
                }
                Add(new DrawnText(lat, lon, PendingText.Trim()) { Color = Color });
                return;

            case DrawingKind.Circle:
                _pending.Add(new PlacePoint(lat, lon));
                if (_pending.Count < 2)
                {
                    Hint();
                    Rebuild();
                    return;
                }
                double radius = GeoMath.DistanceM(
                    _pending[0].LatDeg, _pending[0].LonDeg, lat, lon);
                var centre = _pending[0];
                _pending.Clear();
                // A circle of no size is a mis-click, not a shape.
                if (radius < 1)
                {
                    Hint();
                    Rebuild();
                    return;
                }
                Add(new DrawnCircle(centre.LatDeg, centre.LonDeg, radius)
                {
                    Color = Color,
                    WidthPx = WidthPx,
                });
                return;

            default:
                _pending.Add(new PlacePoint(lat, lon));
                Hint();
                Rebuild();
                return;
        }
    }

    /// <summary>
    /// Virtual key codes straight from the map's window, so this is reachable whether the
    /// focus is on the map or the panel beside it.
    /// </summary>
    public bool OnKey(int virtualKey)
    {
        const int Enter = 13, Escape = 27, Backspace = 8, Delete = 46;
        switch (virtualKey)
        {
            case Enter:
                Finish();
                return true;
            case Escape:
                if (!IsDrawing) return false;
                _pending.Clear();
                Hint();
                Rebuild();
                return true;
            case Backspace:
                if (IsDrawing) UndoPoint(); else UndoShape();
                return true;
            case Delete:
                UndoShape();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Close off whatever is being drawn. A shape too small to exist is dropped.</summary>
    public void Finish()
    {
        if (_pending.Count < 2)
        {
            _pending.Clear();
            Hint();
            Rebuild();
            return;
        }

        // Taken and cleared before the shape is added, because adding it refreshes the
        // hint — and a hint computed while the finished points are still pending tells the
        // user to press Enter on a shape they have already finished.
        var points = _pending.ToArray();
        _pending.Clear();

        bool polygon = Kind == DrawingKind.Polygon;
        Add(new DrawnPath(points, Closed: polygon, Filled: polygon)
        {
            Color = Color,
            WidthPx = WidthPx,
        });
    }

    public void UndoPoint()
    {
        if (_pending.Count == 0) return;
        _pending.RemoveAt(_pending.Count - 1);
        Hint();
        Rebuild();
    }

    /// <summary>Remove the most recent finished shape.</summary>
    public void UndoShape()
    {
        if (_shapes.Count == 0) return;
        _shapes.RemoveAt(_shapes.Count - 1);
        Hint();
        Rebuild();
    }

    public void Clear()
    {
        _shapes.Clear();
        _pending.Clear();
        Hint();
        Rebuild();
    }

    /// <summary>
    /// Write the drawing out. The file's own name becomes the placefile title, which is
    /// what a reader sees in a layer list — so a drawing is identifiable later without
    /// having to ask the user to name it twice.
    /// </summary>
    public void Save(string path) =>
        File.WriteAllText(path, DrawingFile.Write(
            new DrawingDocument(Path.GetFileNameWithoutExtension(path), [.. _shapes])));

    /// <summary>
    /// Load shapes from a placefile. They are added rather than replacing what is there, so
    /// tracing over an imported outline does not mean losing it first.
    /// </summary>
    public int Load(string path)
    {
        var drawing = DrawingFile.Read(File.ReadAllText(path));
        _shapes.AddRange(drawing.Shapes);
        Hint();
        Rebuild();
        return drawing.Shapes.Count;
    }

    private void Add(DrawnShape shape)
    {
        _shapes.Add(shape);
        Hint();
        Rebuild();
    }

    /// <summary>Restate what the next click will do, after the tool has been reconfigured.</summary>
    public void RefreshHint() => Hint();

    private void Hint()
    {
        string message = Kind switch
        {
            DrawingKind.Text => string.IsNullOrWhiteSpace(PendingText)
                ? "Type a label, then click the map to place it."
                : $"Click to place “{PendingText.Trim()}”.",
            DrawingKind.Circle => _pending.Count == 0
                ? "Click the centre of the circle."
                : "Click a point on the edge.",
            _ => _pending.Count switch
            {
                0 => "Click to start. Each click adds a corner.",
                1 => "Click the next corner. Esc abandons it.",
                _ => $"{_pending.Count} points — Enter finishes, Backspace takes one back, Esc abandons.",
            },
        };
        HintChanged?.Invoke(message);
    }

    /// <summary>
    /// Rebuild the whole layer. Drawings are counted in tens, not thousands, so rebuilding
    /// all of them costs less than the bookkeeping needed to rebuild one.
    /// </summary>
    private void Rebuild()
    {
        var geometry = new OverlayGeometry();
        var labels = new List<MapView.MapLabel>();

        foreach (var shape in _shapes)
            Emit(geometry, labels, shape);

        // The shape being drawn, shown as it is built so the user can see what they have.
        if (_pending.Count > 0)
        {
            var preview = Kind == DrawingKind.Circle && _pending.Count == 1
                ? null
                : new DrawnPath([.. _pending]) { Color = Color, WidthPx = WidthPx };
            if (preview is not null) Emit(geometry, labels, preview);

            // A lone point has no line to draw, so mark it or the first click looks lost.
            if (_pending.Count == 1) EmitMarker(geometry, _pending[0]);
        }

        Geometry = geometry.Lines.Count == 0 && geometry.FillTriangles.Count == 0
            ? null
            : geometry;
        Labels = labels;
        Changed?.Invoke();
    }

    private void Emit(
        OverlayGeometry geometry, List<MapView.MapLabel> labels, DrawnShape shape)
    {
        uint rgba = OverlayGeometry.Pack(shape.Color.R, shape.Color.G, shape.Color.B, shape.Color.A);

        switch (shape)
        {
            case DrawnText label:
            {
                var (x, y) = GeoMath.ToMercator(label.LatDeg, label.LonDeg);
                labels.Add(new MapView.MapLabel(x, y, label.Text, 0, 0));
                break;
            }

            case DrawnCircle circle:
                Stroke(geometry, DrawingFile.CirclePoints(circle), rgba, shape.WidthPx);
                break;

            case DrawnPath path:
            {
                if (path.Filled && path.Points.Count >= 3)
                {
                    // Fill well under half opacity: the whole point of drawing over radar
                    // is to still see the radar.
                    uint fill = OverlayGeometry.Pack(
                        shape.Color.R, shape.Color.G, shape.Color.B, (byte)(shape.Color.A / 5));
                    geometry.AddPolygonFill(
                        [.. path.Points.Select(p => GeoMath.ToMercator(p.LatDeg, p.LonDeg))], fill);
                }

                var points = path.Points;
                if (path.Closed && points.Count > 2 && points[0] != points[^1])
                    points = [.. points, points[0]];
                Stroke(geometry, points, rgba, shape.WidthPx);
                break;
            }
        }
    }

    private static void Stroke(
        OverlayGeometry geometry, IReadOnlyList<PlacePoint> points, uint rgba, float widthPx)
    {
        for (int i = 1; i < points.Count; i++)
        {
            var a = GeoMath.ToMercator(points[i - 1].LatDeg, points[i - 1].LonDeg);
            var b = GeoMath.ToMercator(points[i].LatDeg, points[i].LonDeg);
            geometry.Lines.Add((a.X, a.Y, b.X, b.Y, rgba, widthPx));
        }
    }

    /// <summary>
    /// A cross at a single placed point. Sized in Mercator metres at the current zoom so it
    /// stays the same size on screen as the user zooms about deciding where the next one goes.
    /// </summary>
    private void EmitMarker(OverlayGeometry geometry, PlacePoint point)
    {
        var (x, y) = GeoMath.ToMercator(point.LatDeg, point.LonDeg);
        double r = 6 * mapView.Camera.Snapshot().MetersPerPixel;
        uint rgba = OverlayGeometry.Pack(Color.R, Color.G, Color.B, Color.A);
        geometry.Lines.Add(new OverlayLine(x - r, y, x + r, y, rgba, WidthPx, LineCaps.Both));
        geometry.Lines.Add(new OverlayLine(x, y - r, x, y + r, rgba, WidthPx, LineCaps.Both));
    }
}
