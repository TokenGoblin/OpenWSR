using System.IO;
using System.IO.Compression;
using System.Text;
using OpenWSR.Geo;

namespace OpenWSR.App.Tests;

/// <summary>
/// Import dispatches on the file extension and, for an archive, on what is inside it. A
/// public data portal hands you a zip far more often than a bare .shp, so the archive path
/// is the one that has to be right.
/// </summary>
public sealed class ShapeImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openwsr-import").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenWSR.slnx")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, relative);
    }

    private const string Sample = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","properties":{"NAME":"a line"},
           "geometry":{"type":"LineString","coordinates":[[-111.9,40.7],[-111.8,40.8]]}}
        ]}
        """;

    [Theory]
    [InlineData(".geojson")]
    [InlineData(".json")]
    public void GeoJsonLoadsByEitherExtension(string extension)
    {
        var path = Path.Combine(_dir, "sample" + extension);
        File.WriteAllText(path, Sample);

        var features = ShapeImportController.ReadAny(path);

        Assert.Single(features);
        Assert.Equal("a line", features[0]["NAME"]);
    }

    [Fact]
    public void AShapefileLoadsWithItsAttributesFromTheSiblingDbf()
    {
        var shp = Path.Combine(_dir, "states.shp");
        File.Copy(RepoFile("assets/testdata/boundaries/cb_2023_us_state_20m.shp"), shp);
        File.Copy(RepoFile("assets/testdata/boundaries/cb_2023_us_state_20m.dbf"),
            Path.Combine(_dir, "states.dbf"));

        var features = ShapeImportController.ReadAny(shp);

        Assert.Equal(52, features.Count);
        Assert.Equal("Texas", features[0]["NAME"]);
    }

    [Fact]
    public void AShapefileWithNoDbfStillDrawsButHasNoAttributes()
    {
        // Losing the attributes costs labels, not geometry, so it is not worth refusing.
        var shp = Path.Combine(_dir, "lonely.shp");
        File.Copy(RepoFile("assets/testdata/boundaries/cb_2023_us_state_20m.shp"), shp);

        var features = ShapeImportController.ReadAny(shp);

        Assert.Equal(52, features.Count);
        Assert.Empty(features[0].Attributes);
    }

    [Fact]
    public void AZippedShapefilePairsTheDbfByName()
    {
        // The trap: taking "the first .dbf" works on a Census archive, which holds one of
        // each, and silently mismatches on a bundle holding several layers.
        var zip = Path.Combine(_dir, "bundle.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Add(archive, "other.dbf", [1, 2, 3]);
            Add(archive, "states.shp",
                File.ReadAllBytes(RepoFile("assets/testdata/boundaries/cb_2023_us_state_20m.shp")));
            Add(archive, "states.dbf",
                File.ReadAllBytes(RepoFile("assets/testdata/boundaries/cb_2023_us_state_20m.dbf")));
        }

        var features = ShapeImportController.ReadAny(zip);

        Assert.Equal(52, features.Count);
        Assert.Equal("Texas", features[0]["NAME"]);
    }

    [Fact]
    public void AZippedGeoJsonLoadsToo()
    {
        var zip = Path.Combine(_dir, "geo.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            Add(archive, "shapes.geojson", Encoding.UTF8.GetBytes(Sample));

        Assert.Single(ShapeImportController.ReadAny(zip));
    }

    [Fact]
    public void AnArchiveWithNothingReadableSaysWhatItWanted()
    {
        var zip = Path.Combine(_dir, "empty.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            Add(archive, "readme.txt", Encoding.UTF8.GetBytes("nothing here"));

        var ex = Assert.Throws<NotSupportedException>(() => ShapeImportController.ReadAny(zip));
        Assert.Contains(".shp", ex.Message);
    }

    [Fact]
    public void AnUnknownExtensionSaysWhatIsAccepted()
    {
        var path = Path.Combine(_dir, "notes.docx");
        File.WriteAllText(path, "x");

        var ex = Assert.Throws<NotSupportedException>(() => ShapeImportController.ReadAny(path));
        Assert.Contains(".geojson", ex.Message);
    }

    [Fact]
    public void ImportedGeometryProjectsIntoALayer()
    {
        var path = Path.Combine(_dir, "sample.geojson");
        File.WriteAllText(path, Sample);

        var layer = ShapeLayer.From(ShapeImportController.ReadAny(path));

        Assert.Equal(1, layer.PartCount);
        Assert.Equal(2, layer.PointCount);
    }

    private static void Add(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(bytes);
    }
}
