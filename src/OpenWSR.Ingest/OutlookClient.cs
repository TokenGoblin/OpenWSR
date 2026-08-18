using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>A polygon product: SPC outlook area, mesoscale discussion, or watch box.</summary>
public sealed record OutlookArea(
    string Kind,          // "outlook", "mcd", "watch"
    string Category,      // outlook risk name, or "MD 2024" / "Tornado Watch 512"
    int Level,            // outlook DN (2..8); 0 for MCD/watch
    string Detail,
    DateTimeOffset? Expires,
    IReadOnlyList<IReadOnlyList<(double LatDeg, double LonDeg)>> Rings);

/// <summary>One NWS local storm report.</summary>
public sealed record StormReport(
    double LatDeg, double LonDeg,
    string Type, string Magnitude, string City, string County, string State,
    DateTimeOffset TimeUtc, string Remark);

/// <summary>
/// Free national convective products: SPC categorical/tornado/hail/wind outlooks and
/// mesoscale discussions, SPC watch boxes, and NWS local storm reports. All plain
/// GeoJSON with no key; SPC serves its files with a UTF-8 BOM, which is handled here.
/// </summary>
public sealed class OutlookClient : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<OutlookClient>();
    private readonly HttpClient _http;

    /// <summary>SPC categorical risk names by DN value.</summary>
    private static readonly Dictionary<int, string> RiskNames = new()
    {
        [2] = "Marginal", [3] = "Slight", [4] = "Enhanced",
        [5] = "Moderate", [6] = "High", [7] = "High", [8] = "High",
    };

    public OutlookClient(string userAgent)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public async Task<IReadOnlyList<OutlookArea>> GetConvectiveAsync(CancellationToken ct = default)
    {
        var areas = new List<OutlookArea>();
        await AddOutlookAsync(areas,
            "https://www.spc.noaa.gov/products/outlook/day1otlk_cat.lyr.geojson", ct);
        await AddMcdAsync(areas, ct);
        await AddWatchesAsync(areas, ct);
        return areas;
    }

    public async Task<IReadOnlyList<StormReport>> GetStormReportsAsync(
        int hours = 6, CancellationToken ct = default)
    {
        var reports = new List<StormReport>();
        try
        {
            using var json = await FetchAsync(
                $"https://mesonet.agron.iastate.edu/geojson/lsr.geojson?hours={hours}", ct);
            foreach (var feature in json.RootElement.GetProperty("features").EnumerateArray())
            {
                if (!TryPoint(feature, out double lat, out double lon)) continue;
                var p = feature.GetProperty("properties");
                double magnitude = p.TryGetProperty("magf", out var m) && m.ValueKind == JsonValueKind.Number
                    ? m.GetDouble() : 0;
                string type = Str(p, "typetext") ?? "Report";
                DateTimeOffset.TryParse(Str(p, "valid"), out var time);
                reports.Add(new StormReport(
                    lat, lon, type,
                    magnitude > 0 ? FormatMagnitude(type, magnitude) : "",
                    Str(p, "city") ?? "", Str(p, "county") ?? "", Str(p, "state") ?? "",
                    time, Str(p, "remark") ?? ""));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Storm report fetch failed");
        }
        return reports;
    }

    private static string FormatMagnitude(string type, double magnitude) =>
        type.Contains("HAIL", StringComparison.OrdinalIgnoreCase) ? $"{magnitude:0.##}\""
        : type.Contains("SNOW", StringComparison.OrdinalIgnoreCase) ? $"{magnitude:0.#}\""
        : type.Contains("RAIN", StringComparison.OrdinalIgnoreCase) ? $"{magnitude:0.##}\""
        : $"{magnitude:0} mph";

    private async Task AddOutlookAsync(List<OutlookArea> areas, string url, CancellationToken ct)
    {
        try
        {
            using var json = await FetchAsync(url, ct);
            foreach (var feature in json.RootElement.GetProperty("features").EnumerateArray())
            {
                var rings = ReadRings(feature);
                if (rings.Count == 0) continue;
                var p = feature.GetProperty("properties");
                int level = p.TryGetProperty("DN", out var dn) && dn.ValueKind == JsonValueKind.Number
                    ? dn.GetInt32() : 0;
                DateTimeOffset? expires = DateTimeOffset.TryParse(Str(p, "EXPIRE_ISO"), out var e) ? e : null;
                areas.Add(new OutlookArea(
                    "outlook",
                    RiskNames.TryGetValue(level, out var name) ? $"{name} risk" : $"Risk {level}",
                    level, "SPC Day 1 convective outlook", expires, rings));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "SPC outlook fetch failed");
        }
    }

    private async Task AddMcdAsync(List<OutlookArea> areas, CancellationToken ct)
    {
        try
        {
            using var json = await FetchAsync(
                "https://mesonet.agron.iastate.edu/api/1/nws/spc_mcd.geojson", ct);
            foreach (var feature in json.RootElement.GetProperty("features").EnumerateArray())
            {
                var rings = ReadRings(feature);
                if (rings.Count == 0) continue;
                var p = feature.GetProperty("properties");
                int number = p.TryGetProperty("num", out var n) && n.ValueKind == JsonValueKind.Number
                    ? n.GetInt32() : 0;
                int confidence = p.TryGetProperty("watch_confidence", out var c) &&
                                 c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
                DateTimeOffset? expires = DateTimeOffset.TryParse(Str(p, "expire"), out var e) ? e : null;
                var concerning = Str(p, "concerning") ?? "Mesoscale discussion";
                areas.Add(new OutlookArea(
                    "mcd", $"MD {number}", 0,
                    confidence >= 0 ? $"{concerning} — watch probability {confidence}%" : concerning,
                    expires, rings));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "SPC mesoscale discussion fetch failed");
        }
    }

    private async Task AddWatchesAsync(List<OutlookArea> areas, CancellationToken ct)
    {
        try
        {
            using var json = await FetchAsync(
                "https://mesonet.agron.iastate.edu/api/1/spc_watch_outline.geojson", ct);
            foreach (var feature in json.RootElement.GetProperty("features").EnumerateArray())
            {
                var rings = ReadRings(feature);
                if (rings.Count == 0) continue;
                var p = feature.GetProperty("properties");
                var type = Str(p, "type") ?? "";
                int number = p.TryGetProperty("num", out var n) && n.ValueKind == JsonValueKind.Number
                    ? n.GetInt32() : 0;
                DateTimeOffset? expires = DateTimeOffset.TryParse(Str(p, "utc_expired"), out var e) ? e : null;
                bool tornado = type.Contains("TOR", StringComparison.OrdinalIgnoreCase);
                areas.Add(new OutlookArea(
                    "watch",
                    tornado ? $"Tornado Watch {number}" : $"Severe T-Storm Watch {number}",
                    tornado ? 6 : 4,
                    tornado ? "Tornado watch in effect" : "Severe thunderstorm watch in effect",
                    expires, rings));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "SPC watch fetch failed");
        }
    }

    // ---- GeoJSON helpers ----

    private async Task<JsonDocument> FetchAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        // SPC serves these with a UTF-8 BOM, which System.Text.Json rejects.
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return JsonDocument.Parse(bytes.AsMemory(start));
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool TryPoint(JsonElement feature, out double lat, out double lon)
    {
        lat = lon = 0;
        if (!feature.TryGetProperty("geometry", out var geometry) ||
            geometry.ValueKind != JsonValueKind.Object) return false;
        if (geometry.GetProperty("type").GetString() != "Point") return false;
        var coordinates = geometry.GetProperty("coordinates");
        lon = coordinates[0].GetDouble();
        lat = coordinates[1].GetDouble();
        return true;
    }

    /// <summary>Exterior rings of a Polygon or MultiPolygon, as (lat, lon) pairs.</summary>
    private static List<IReadOnlyList<(double LatDeg, double LonDeg)>> ReadRings(JsonElement feature)
    {
        var rings = new List<IReadOnlyList<(double, double)>>();
        if (!feature.TryGetProperty("geometry", out var geometry) ||
            geometry.ValueKind != JsonValueKind.Object)
            return rings;

        var type = geometry.GetProperty("type").GetString();
        var coordinates = geometry.GetProperty("coordinates");
        if (type == "Polygon")
            AddExterior(rings, coordinates);
        else if (type == "MultiPolygon")
            foreach (var polygon in coordinates.EnumerateArray())
                AddExterior(rings, polygon);
        return rings;
    }

    private static void AddExterior(
        List<IReadOnlyList<(double, double)>> rings, JsonElement polygon)
    {
        foreach (var ring in polygon.EnumerateArray())
        {
            var points = new List<(double, double)>();
            foreach (var position in ring.EnumerateArray())
                points.Add((position[1].GetDouble(), position[0].GetDouble()));
            if (points.Count >= 3) rings.Add(points);
            break; // exterior ring only
        }
    }

    public void Dispose() => _http.Dispose();
}
