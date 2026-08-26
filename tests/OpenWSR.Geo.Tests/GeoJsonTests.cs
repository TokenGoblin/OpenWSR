using System.Text;
using OpenWSR.Geo;

namespace OpenWSR.Geo.Tests;

/// <summary>
/// GeoJSON is a spec rather than a binary layout, so these assert the parts of it that are
/// easy to get wrong and that fail silently when you do — coordinate order above all.
/// </summary>
public sealed class GeoJsonTests
{
    [Fact]
    public void CoordinatesAreLongitudeThenLatitude()
    {
        // RFC 7946 §3.1.1. The order is the opposite of how a position is spoken, and
        // reading it backwards throws nothing — it just moves the data.
        const string json = """
            {"type":"Point","coordinates":[-97.5,35.3]}
            """;

        var point = GeoJson.Read(json).Single();

        Assert.Equal(ShapeKind.Point, point.Kind);
        Assert.Equal(35.3, point.Parts[0][0].LatDeg, 6);
        Assert.Equal(-97.5, point.Parts[0][0].LonDeg, 6);
    }

    [Fact]
    public void AByteOrderMarkIsStripped()
    {
        // System.Text.Json rejects a BOM rather than skipping it, and SPC serves its
        // GeoJSON with one — so this is a real source, not a hypothetical.
        const string json = """
            {"type":"Point","coordinates":[1,2]}
            """;
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();

        Assert.Equal(3, Encoding.UTF8.GetPreamble().Length);
        var point = GeoJson.Read(withBom).Single();
        Assert.Equal(2.0, point.Parts[0][0].LatDeg);
    }

    [Fact]
    public void AFeatureCollectionKeepsPropertiesWithTheirGeometry()
    {
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature",
               "properties":{"NAME":"Cleveland","FIPS":"40027","POP":295528},
               "geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}
            ]}
            """;

        var feature = GeoJson.Read(json).Single();

        Assert.Equal("Cleveland", feature["NAME"]);
        Assert.Equal("40027", feature["FIPS"]);   // a FIPS code is a string, leading zeros and all
        Assert.Equal("295528", feature["POP"]);   // numbers come back as their text
        Assert.Null(feature["MISSING"]);
    }

    [Fact]
    public void PolygonHolesAreKeptAsTheirOwnParts()
    {
        // The inner boundary of a doughnut is as real a line as the outer one when what you
        // are drawing is outlines.
        const string json = """
            {"type":"Polygon","coordinates":[
              [[0,0],[10,0],[10,10],[0,10],[0,0]],
              [[2,2],[4,2],[4,4],[2,4],[2,2]]
            ]}
            """;

        var polygon = GeoJson.Read(json).Single();

        Assert.Equal(ShapeKind.Polygon, polygon.Kind);
        Assert.Equal(2, polygon.Parts.Count);
        Assert.Equal(5, polygon.Parts[1].Count);
    }

    [Fact]
    public void AMultiPolygonBecomesOneFeatureOfManyRings()
    {
        const string json = """
            {"type":"MultiPolygon","coordinates":[
              [[[0,0],[1,0],[1,1],[0,0]]],
              [[[5,5],[6,5],[6,6],[5,5]]]
            ]}
            """;

        var feature = GeoJson.Read(json).Single();
        Assert.Equal(2, feature.Parts.Count);
    }

    [Fact]
    public void LineStringsAreReadAsLines()
    {
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{},
               "geometry":{"type":"MultiLineString","coordinates":[
                 [[0,0],[1,1],[2,2]],
                 [[9,9],[8,8]]]}}
            ]}
            """;

        var feature = GeoJson.Read(json).Single();

        Assert.Equal(ShapeKind.PolyLine, feature.Kind);
        Assert.Equal(2, feature.Parts.Count);
        Assert.Equal(3, feature.Parts[0].Count);
    }

    [Fact]
    public void AGeometryCollectionIsFlattened()
    {
        const string json = """
            {"type":"GeometryCollection","geometries":[
              {"type":"Point","coordinates":[1,2]},
              {"type":"LineString","coordinates":[[0,0],[1,1]]}
            ]}
            """;

        var features = GeoJson.Read(json);

        Assert.Equal(2, features.Count);
        Assert.Equal(ShapeKind.Point, features[0].Kind);
        Assert.Equal(ShapeKind.PolyLine, features[1].Kind);
    }

    [Fact]
    public void AThirdOrdinateIsIgnoredRatherThanRefused()
    {
        // Altitude is legal in a position and nothing here needs it.
        const string json = """
            {"type":"Point","coordinates":[-97.5,35.3,411.0]}
            """;

        var point = GeoJson.Read(json).Single();
        Assert.Equal(35.3, point.Parts[0][0].LatDeg, 6);
    }

    [Fact]
    public void GeometryWithTooFewPointsIsDropped()
    {
        // A "polygon" of two positions cannot be drawn and is not worth an exception.
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{},
               "geometry":{"type":"Polygon","coordinates":[[[0,0],[1,1]]]}},
              {"type":"Feature","properties":{},
               "geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}
            ]}
            """;

        Assert.Single(GeoJson.Read(json));
    }

    [Fact]
    public void AFeatureWithNullGeometryIsSkipped()
    {
        // api.weather.gov emits these for zone-based alerts, which carry no polygon.
        const string json = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"NAME":"zone only"},"geometry":null}
            ]}
            """;

        Assert.Empty(GeoJson.Read(json));
    }

    [Fact]
    public void SomethingThatIsNotGeoJsonThrows()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => GeoJson.Read("not json at all"));
    }

    [Fact]
    public void ValidJsonThatIsNotGeoJsonIsEmptyRatherThanAnError()
    {
        Assert.Empty(GeoJson.Read("""{"hello":"world"}"""));
        Assert.Empty(GeoJson.Read("[1,2,3]"));
    }
}
