using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace OpenWSR.Ingest;

public sealed record ActiveAlert(
    string Id,
    string Event,
    string Headline,
    string Severity,
    DateTimeOffset? Expires,
    string Description,
    string? Instruction,
    IReadOnlyList<IReadOnlyList<(double LatDeg, double LonDeg)>> Polygons);

/// <summary>
/// api.weather.gov active-alerts poller. The API requires a descriptive User-Agent with
/// contact info (or it 403s) and asks for polling no faster than 60 s — both enforced here.
/// Only storm-based (polygon-carrying) alerts are returned; zone-based alerts have no
/// geometry to draw.
/// </summary>
public sealed class AlertsClient : IDisposable
{
    private const string Endpoint = "https://api.weather.gov/alerts/active?status=actual";
    private static readonly ILogger Log = Serilog.Log.ForContext<AlertsClient>();

    private readonly HttpClient _http;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private IReadOnlyList<ActiveAlert> _lastResult = [];

    public AlertsClient(string userAgent)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");
    }

    /// <summary>Fetch active alerts; calls within 60 s of the last fetch return the cached set.</summary>
    public async Task<IReadOnlyList<ActiveAlert>> GetActiveAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastFetchUtc < TimeSpan.FromSeconds(60))
            return Prune(_lastResult);

        var started = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _http.GetAsync(Endpoint, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var alerts = new List<ActiveAlert>();
        foreach (var feature in json.RootElement.GetProperty("features").EnumerateArray())
        {
            var alert = ParseFeature(feature);
            if (alert is not null)
                alerts.Add(alert);
        }

        Log.Debug("alerts/active: {Count} polygon alerts in {Ms} ms", alerts.Count, started.ElapsedMilliseconds);
        _lastFetchUtc = DateTime.UtcNow;
        _lastResult = alerts;
        return Prune(alerts);
    }

    private static ActiveAlert? ParseFeature(JsonElement feature)
    {
        if (!feature.TryGetProperty("geometry", out var geometry) ||
            geometry.ValueKind != JsonValueKind.Object)
            return null;

        var polygons = new List<IReadOnlyList<(double, double)>>();
        var type = geometry.GetProperty("type").GetString();
        var coordinates = geometry.GetProperty("coordinates");
        if (type == "Polygon")
            AddPolygon(polygons, coordinates);
        else if (type == "MultiPolygon")
            foreach (var polygon in coordinates.EnumerateArray())
                AddPolygon(polygons, polygon);
        if (polygons.Count == 0)
            return null;

        var properties = feature.GetProperty("properties");
        string? Get(string name) =>
            properties.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        DateTimeOffset? expires = null;
        // "ends" outlives "expires" for alerts that are re-issued; prefer it when present.
        if (DateTimeOffset.TryParse(Get("ends") ?? Get("expires"), out var parsedExpiry))
            expires = parsedExpiry;

        return new ActiveAlert(
            Get("id") ?? feature.GetProperty("id").GetString() ?? Guid.NewGuid().ToString(),
            Get("event") ?? "Alert",
            Get("headline") ?? Get("event") ?? "Alert",
            Get("severity") ?? "Unknown",
            expires,
            Get("description") ?? "",
            Get("instruction"),
            polygons);
    }

    private static void AddPolygon(List<IReadOnlyList<(double, double)>> polygons, JsonElement rings)
    {
        // GeoJSON: [exteriorRing, holes...]; warning polygons have no holes worth honoring.
        foreach (var ring in rings.EnumerateArray())
        {
            var points = new List<(double, double)>();
            foreach (var position in ring.EnumerateArray())
            {
                double lon = position[0].GetDouble();
                double lat = position[1].GetDouble();
                points.Add((lat, lon));
            }
            if (points.Count >= 3)
                polygons.Add(points);
            break; // exterior ring only
        }
    }

    private static IReadOnlyList<ActiveAlert> Prune(IReadOnlyList<ActiveAlert> alerts)
    {
        var now = DateTimeOffset.UtcNow;
        return [.. alerts.Where(a => a.Expires is null || a.Expires > now)];
    }

    public void Dispose() => _http.Dispose();
}
