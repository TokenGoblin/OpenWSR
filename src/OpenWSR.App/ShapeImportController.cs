using System.IO;
using System.IO.Compression;
using OpenWSR.Geo;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>One imported file, as the panel lists it.</summary>
public sealed class ImportedShapes
{
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public required uint Colour { get; init; }
    public bool Enabled { get; set; } = true;
    public string Status { get; set; } = "";
    internal ShapeLayer? Layer { get; set; }
}

/// <summary>
/// Loads user-supplied vector files — GeoJSON, shapefiles, or a zip of either — and draws
/// them as toggleable overlays.
/// </summary>
/// <remarks>
/// This is the same machinery the boundaries layer runs on, pointed at a file the user chose
/// instead of at the Census. Nothing about a county outline is special; once a reader turns
/// bytes into <see cref="ShapeFeature"/>s, <see cref="ShapeLayer"/> draws them at any zoom.
///
/// <para>Each import gets its own colour from a fixed cycle rather than a random one, so two
/// files loaded in the same order always look the same, and so a screenshot means the same
/// thing tomorrow.</para>
/// </remarks>
public sealed class ShapeImportController
{
    // Deliberately not red, amber or green: those already mean tornado, severe and flood on
    // this map, and an imported county list must not read as a warning.
    private static readonly uint[] Palette =
    [
        OverlayGeometry.Pack(120, 200, 255, 220),
        OverlayGeometry.Pack(200, 150, 255, 220),
        OverlayGeometry.Pack(255, 170, 220, 220),
        OverlayGeometry.Pack(150, 230, 220, 220),
        OverlayGeometry.Pack(200, 200, 240, 220),
    ];

    private readonly MapView _mapView;
    private readonly List<ImportedShapes> _files = [];
    private int _nextColour;
    private int _buildGeneration;
    private double _builtAtMetresPerPixel;
    private double _builtAtCenterX, _builtAtCenterY;

    public ShapeImportController(MapView mapView) => _mapView = mapView;

    public IReadOnlyList<ImportedShapes> Files => _files;
    public OverlayGeometry? Geometry { get; private set; }

    public event Action? Changed;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorRaised;

    /// <summary>File-dialog filter covering everything <see cref="AddAsync"/> accepts.</summary>
    public const string FileFilter =
        "Vector overlays|*.geojson;*.json;*.shp;*.zip|" +
        "GeoJSON (*.geojson;*.json)|*.geojson;*.json|" +
        "Shapefile (*.shp)|*.shp|" +
        "Zipped shapefile or GeoJSON (*.zip)|*.zip|" +
        "All files|*.*";

    public async Task AddAsync(string path)
    {
        // Adding the same file twice drew two layers in different palette colours, and only
        // one of them was ever persisted — so removing "it" left a copy on screen that did
        // not come back next launch.
        if (_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            StatusChanged?.Invoke($"{Path.GetFileName(path)} is already loaded.");
            return;
        }

        var entry = new ImportedShapes
        {
            Path = path,
            DisplayName = Path.GetFileName(path),
            Colour = Palette[_nextColour++ % Palette.Length],
        };

        try
        {
            var features = await Task.Run(() => ReadAny(path));
            if (features.Count == 0)
            {
                ErrorRaised?.Invoke($"{entry.DisplayName} has no geometry this can draw.");
                return;
            }

            entry.Layer = await Task.Run(() => ShapeLayer.From(features));
            entry.Status = $"{features.Count} features, {entry.Layer.PointCount:N0} points";
            _files.Add(entry);
            StatusChanged?.Invoke($"{entry.DisplayName}: {entry.Status}.");
            Changed?.Invoke();
            Rebuild();
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke($"Could not read {entry.DisplayName}: {ex.Message}");
        }
    }

    public void Remove(ImportedShapes entry)
    {
        _files.Remove(entry);
        Changed?.Invoke();
        Rebuild();
    }

    public void SetEnabled(ImportedShapes entry, bool enabled)
    {
        entry.Enabled = enabled;
        Rebuild();
    }

    /// <summary>
    /// Read whatever the extension says this is. A zip is opened and the first readable
    /// member used, which is what a download from a public data portal actually looks like.
    /// </summary>
    internal static IReadOnlyList<ShapeFeature> ReadAny(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".geojson":
            case ".json":
                return GeoJson.Read(File.ReadAllBytes(path));

            case ".shp":
            {
                // The .dbf is optional, and its absence costs only the attributes.
                var dbf = Path.ChangeExtension(path, ".dbf");
                return Shapefile.Read(
                    File.ReadAllBytes(path),
                    File.Exists(dbf) ? File.ReadAllBytes(dbf) : null);
            }

            case ".zip":
                return ReadZip(path);

            default:
                throw new NotSupportedException(
                    "Expected a .geojson, .json, .shp or .zip file.");
        }
    }

    private static IReadOnlyList<ShapeFeature> ReadZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);

        var shp = archive.Entries.FirstOrDefault(
            e => e.Name.EndsWith(".shp", StringComparison.OrdinalIgnoreCase));
        if (shp is not null)
        {
            // Match the .dbf by base name, not by "the first .dbf": a Census archive holds
            // one of each, but a bundle of several layers holds several.
            var stem = Path.GetFileNameWithoutExtension(shp.Name);
            var dbf = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals(stem + ".dbf", StringComparison.OrdinalIgnoreCase));
            return Shapefile.Read(ReadAll(shp), dbf is null ? null : ReadAll(dbf));
        }

        var json = archive.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase) ||
            e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (json is not null) return GeoJson.Read(ReadAll(json));

        throw new NotSupportedException("The archive holds no .shp or .geojson.");
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <inheritdoc cref="BoundariesController.NotifyViewChanged"/>
    public void NotifyViewChanged()
    {
        if (_files.Count == 0) return;
        var cam = _mapView.Camera.Snapshot();
        if (cam.ViewportWidth <= 0 || cam.ViewportHeight <= 0) return;

        bool zoomed = Math.Abs(cam.MetersPerPixel - _builtAtMetresPerPixel)
                      / Math.Max(cam.MetersPerPixel, 1e-6) > 0.05;
        // Per axis — see BoundariesController.NotifyViewChanged.
        double pannedX = Math.Abs(cam.CenterX - _builtAtCenterX)
                       / (cam.ViewportWidth * cam.MetersPerPixel);
        double pannedY = Math.Abs(cam.CenterY - _builtAtCenterY)
                       / (cam.ViewportHeight * cam.MetersPerPixel);
        if (!zoomed && Math.Max(pannedX, pannedY) < 0.25) return;
        Rebuild();
    }

    private void Rebuild()
    {
        var active = _files.Where(f => f.Enabled && f.Layer is not null).ToList();
        if (active.Count == 0)
        {
            Geometry = null;
            Changed?.Invoke();
            return;
        }

        var cam = _mapView.Camera.Snapshot();
        _builtAtMetresPerPixel = cam.MetersPerPixel;
        _builtAtCenterX = cam.CenterX;
        _builtAtCenterY = cam.CenterY;

        int generation = ++_buildGeneration;
        _ = Task.Run(() =>
        {
            var geometry = new OverlayGeometry();
            foreach (var file in active)
                file.Layer!.Append(geometry, cam, file.Colour, 1.6f);

            if (generation != _buildGeneration) return;
            Geometry = geometry;
            Changed?.Invoke();
        });
    }
}
