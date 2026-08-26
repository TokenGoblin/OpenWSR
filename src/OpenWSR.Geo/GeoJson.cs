using System.Text;
using System.Text.Json;

namespace OpenWSR.Geo;

/// <summary>
/// Reads GeoJSON (RFC 7946) into the same <see cref="ShapeFeature"/> a shapefile produces,
/// so anything that can draw one can draw the other.
/// </summary>
/// <remarks>
/// <b>Coordinates are [longitude, latitude]</b>, in that order — the opposite of how they are
/// spoken and the opposite of the (lat, lon) pairs used everywhere else in this codebase. It
/// is written into the spec precisely because implementations kept disagreeing, and getting
/// it backwards throws nothing: it silently moves the data somewhere else on Earth.
///
/// <para><b>A leading byte-order mark has to be stripped.</b> <c>System.Text.Json</c> rejects
/// one outright rather than skipping it, and SPC serves its GeoJSON with a UTF-8 BOM — so a
/// reader that does not handle this fails on a real, current, official source.</para>
///
/// <para>Rings of a polygon are all kept, holes included. These are drawn as outlines, and the
/// inner boundary of a doughnut is as real a line as the outer one.</para>
/// </remarks>
public static class GeoJson
{
    /// <summary>Read a document. Returns empty for anything with no geometry in it.</summary>
    public static IReadOnlyList<ShapeFeature> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 0 && json[0] == '﻿') json = json[1..];

        using var document = JsonDocument.Parse(json);
        var features = new List<ShapeFeature>();
        AddNode(features, document.RootElement, null);
        return features;
    }

    /// <summary>Read raw bytes, stripping a UTF-8 BOM if the file carries one.</summary>
    public static IReadOnlyList<ShapeFeature> Read(byte[] utf8)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        int start = utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF ? 3 : 0;
        return Read(Encoding.UTF8.GetString(utf8, start, utf8.Length - start));
    }

    private static void AddNode(
        List<ShapeFeature> into, JsonElement node, IReadOnlyDictionary<string, string>? inherited)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (!node.TryGetProperty("type", out var typeElement)) return;

        switch (typeElement.GetString())
        {
            case "FeatureCollection":
                if (node.TryGetProperty("features", out var list)
                    && list.ValueKind == JsonValueKind.Array)
                    foreach (var child in list.EnumerateArray())
                        AddNode(into, child, inherited);
                return;

            case "Feature":
            {
                var properties = ReadProperties(node);
                if (node.TryGetProperty("geometry", out var geometry))
                    AddNode(into, geometry, properties);
                return;
            }

            case "GeometryCollection":
                if (node.TryGetProperty("geometries", out var geometries)
                    && geometries.ValueKind == JsonValueKind.Array)
                    foreach (var child in geometries.EnumerateArray())
                        AddNode(into, child, inherited);
                return;

            default:
                if (ReadGeometry(node, inherited) is { } feature) into.Add(feature);
                return;
        }
    }

    private static ShapeFeature? ReadGeometry(
        JsonElement geometry, IReadOnlyDictionary<string, string>? properties)
    {
        if (!geometry.TryGetProperty("type", out var typeElement)) return null;
        if (!geometry.TryGetProperty("coordinates", out var coordinates)) return null;

        var attributes = properties
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<IReadOnlyList<(double LatDeg, double LonDeg)>>();

        switch (typeElement.GetString())
        {
            case "Point":
                if (TryPosition(coordinates, out var point)) parts.Add([point]);
                return Done(ShapeKind.Point, parts, attributes);

            case "MultiPoint":
            {
                var points = ReadPositions(coordinates);
                if (points.Count > 0) parts.Add(points);
                return Done(ShapeKind.MultiPoint, parts, attributes);
            }

            case "LineString":
                AddRun(parts, coordinates, 2);
                return Done(ShapeKind.PolyLine, parts, attributes);

            case "MultiLineString":
                foreach (var line in Each(coordinates)) AddRun(parts, line, 2);
                return Done(ShapeKind.PolyLine, parts, attributes);

            case "Polygon":
                foreach (var ring in Each(coordinates)) AddRun(parts, ring, 3);
                return Done(ShapeKind.Polygon, parts, attributes);

            case "MultiPolygon":
                foreach (var polygon in Each(coordinates))
                    foreach (var ring in Each(polygon))
                        AddRun(parts, ring, 3);
                return Done(ShapeKind.Polygon, parts, attributes);

            default:
                return null;
        }
    }

    private static ShapeFeature? Done(
        ShapeKind kind,
        List<IReadOnlyList<(double LatDeg, double LonDeg)>> parts,
        IReadOnlyDictionary<string, string> attributes) =>
        parts.Count == 0 ? null : new ShapeFeature(kind, parts, attributes);

    private static IEnumerable<JsonElement> Each(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    private static void AddRun(
        List<IReadOnlyList<(double LatDeg, double LonDeg)>> parts,
        JsonElement element, int minimum)
    {
        var points = ReadPositions(element);
        if (points.Count >= minimum) parts.Add(points);
    }

    private static List<(double LatDeg, double LonDeg)> ReadPositions(JsonElement element)
    {
        var points = new List<(double, double)>();
        foreach (var position in Each(element))
            if (TryPosition(position, out var point))
                points.Add(point);
        return points;
    }

    private static bool TryPosition(JsonElement element, out (double LatDeg, double LonDeg) point)
    {
        point = default;
        if (element.ValueKind != JsonValueKind.Array) return false;

        // A position may carry a third ordinate (altitude); it is ignored, not an error.
        int count = element.GetArrayLength();
        if (count < 2) return false;
        if (element[0].ValueKind != JsonValueKind.Number ||
            element[1].ValueKind != JsonValueKind.Number) return false;

        point = (element[1].GetDouble(), element[0].GetDouble()); // [lon, lat]
        return true;
    }

    private static Dictionary<string, string> ReadProperties(JsonElement feature)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!feature.TryGetProperty("properties", out var element)
            || element.ValueKind != JsonValueKind.Object)
            return properties;

        foreach (var property in element.EnumerateObject())
            properties[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                // Objects and arrays keep their raw JSON rather than being flattened to
                // "System.Object" — a label may legitimately live one level down.
                _ => property.Value.GetRawText(),
            };
        return properties;
    }
}
