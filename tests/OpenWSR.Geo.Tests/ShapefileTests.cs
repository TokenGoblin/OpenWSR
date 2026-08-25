using OpenWSR.Geo;

namespace OpenWSR.Geo.Tests;

/// <summary>
/// Checked against <b>pyshp 3.1.6</b> reading the same file — the project's rule for a
/// binary format is to verify against an independent implementation rather than against
/// our own output. The fixture is the US Census 2023 cartographic state boundaries at
/// 1:20,000,000, which is small enough to commit and has every awkward case in it: a
/// 47-part record, string attributes that must not be parsed as numbers, and rings that
/// close on a repeated first point.
/// </summary>
public sealed class ShapefileTests
{
    private static string Path(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "OpenWSR.slnx")))
            dir = dir.Parent!;
        if (dir is null) throw new InvalidOperationException("Repo root not found.");
        return System.IO.Path.Combine(dir.FullName, "assets", "testdata", "boundaries", name);
    }

    private static IReadOnlyList<ShapeFeature> States() => Shapefile.Read(
        File.ReadAllBytes(Path("cb_2023_us_state_20m.shp")),
        File.ReadAllBytes(Path("cb_2023_us_state_20m.dbf")));

    [Fact]
    public void ItReadsEveryRecord()
    {
        // pyshp: len(reader) == 52 (50 states, DC, Puerto Rico).
        Assert.Equal(52, States().Count);
    }

    [Fact]
    public void EveryRecordIsAPolygon()
    {
        Assert.All(States(), f => Assert.Equal(ShapeKind.Polygon, f.Kind));
    }

    [Fact]
    public void ThePointTotalMatchesTheReference()
    {
        // pyshp: sum(len(s.points) for s in reader.iterShapes()) == 13698.
        int points = States().Sum(f => f.Parts.Sum(p => p.Count));
        Assert.Equal(13698, points);
    }

    [Theory]
    // pyshp, by record index — the order the file is in, which is not alphabetical.
    [InlineData(0, "Texas", "TX", "48", 1, 551)]
    [InlineData(1, "California", "CA", "06", 6, 468)]
    [InlineData(25, "Nevada", "NV", "32", 1, 144)]
    [InlineData(51, "Arizona", "AZ", "04", 1, 159)]
    public void RecordsMatchTheReference(
        int index, string name, string abbreviation, string geoid, int parts, int points)
    {
        var feature = States()[index];

        Assert.Equal(name, feature["NAME"]);
        Assert.Equal(abbreviation, feature["STUSPS"]);
        Assert.Equal(parts, feature.Parts.Count);
        Assert.Equal(points, feature.Parts.Sum(p => p.Count));

        // "06" must survive as a string. Read as a number it becomes 6, and a FIPS code
        // that has lost its leading zero no longer joins to anything.
        Assert.Equal(geoid, feature["GEOID"]);
    }

    [Fact]
    public void AMultiPartRecordKeepsItsPartsSeparate()
    {
        // Alaska is one record of 47 parts — the Aleutians and the panhandle islands.
        // Flattening them into one ring would draw a line from each island to the next.
        var alaska = States().Single(f => f["NAME"] == "Alaska");

        Assert.Equal(47, alaska.Parts.Count);
        Assert.Equal(2269, alaska.Parts.Sum(p => p.Count));
        Assert.Equal(9, alaska.Parts[0].Count);   // pyshp part starts [0, 9, 23, 30, ...]
        Assert.Equal(14, alaska.Parts[1].Count);
        Assert.Equal(7, alaska.Parts[2].Count);
    }

    [Fact]
    public void CoordinatesAreLatitudeAndLongitudeTheRightWayRound()
    {
        // pyshp gives (x, y) = (-106.623445, 31.914034) for the first point of Texas, and
        // x is longitude. Swapping them is the classic shapefile bug and it does not throw:
        // it silently puts the United States in Somalia.
        var texas = States()[0];
        var (lat, lon) = texas.Parts[0][0];

        Assert.Equal(31.914034, lat, 6);
        Assert.Equal(-106.623445, lon, 6);
    }

    [Fact]
    public void RingsCloseOnARepeatedFirstPoint()
    {
        var texas = States()[0];
        var ring = texas.Parts[0];

        Assert.Equal(ring[0], ring[^1]);
    }

    [Fact]
    public void EveryPointIsSomewhereReal()
    {
        foreach (var feature in States())
            foreach (var part in feature.Parts)
                foreach (var (lat, lon) in part)
                {
                    Assert.InRange(lat, -90, 90);
                    Assert.InRange(lon, -180, 180);
                }
    }

    [Fact]
    public void GeometryReadsWithoutAttributes()
    {
        // The .dbf is optional; a caller that only wants outlines should not need it.
        var features = Shapefile.Read(File.ReadAllBytes(Path("cb_2023_us_state_20m.shp")));

        Assert.Equal(52, features.Count);
        Assert.Empty(features[0].Attributes);
        Assert.Equal(551, features[0].Parts.Sum(p => p.Count));
    }

    [Fact]
    public void SomethingThatIsNotAShapefileSaysSo()
    {
        var bytes = new byte[200];
        bytes[0] = 0xDE;
        bytes[1] = 0xAD;

        var ex = Assert.Throws<InvalidDataException>(() => Shapefile.Read(bytes));
        Assert.Contains("file code", ex.Message);
    }

    [Fact]
    public void ATruncatedFileStopsAtTheLastWholeRecord()
    {
        // A half-written download should give back what is readable rather than throwing —
        // the alternative is one bad byte costing every boundary on screen.
        var whole = File.ReadAllBytes(Path("cb_2023_us_state_20m.shp"));
        var cut = whole.AsSpan(0, whole.Length / 2).ToArray();

        var features = Shapefile.Read(cut);
        Assert.InRange(features.Count, 1, 51);
        Assert.All(features, f => Assert.NotEmpty(f.Parts));
    }
}
