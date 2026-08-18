using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace OpenWSR.Ingest;

public sealed record Place(string Name, double LatDeg, double LonDeg);

/// <summary>
/// Turns what a person types — "Orem, UT", "84058", "40.29,-111.69" — into a point.
/// Uses OSM Nominatim, whose usage policy requires a identifying User-Agent and at most
/// one request per second; both are enforced here. Coordinates are parsed locally so the
/// common case never touches the network.
/// </summary>
public sealed class Geocoder : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<Geocoder>();
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public Geocoder(string userAgent)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public async Task<Place?> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0) return null;

        if (TryParseCoordinates(query, out var point))
            return point;

        await _rateGate.WaitAsync(ct);
        try
        {
            var sinceLast = DateTime.UtcNow - _lastRequestUtc;
            if (sinceLast < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - sinceLast, ct);
            _lastRequestUtc = DateTime.UtcNow;

            var url = "https://nominatim.openstreetmap.org/search" +
                      $"?q={Uri.EscapeDataString(query)}&format=json&limit=1&countrycodes=us";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (json.RootElement.GetArrayLength() == 0) return null;
            var first = json.RootElement[0];
            double lat = double.Parse(first.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture);
            double lon = double.Parse(first.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture);
            var name = first.GetProperty("display_name").GetString() ?? query;
            // "84058, Orem, Utah County, Utah, United States" -> "84058, Orem"
            var parts = name.Split(',', StringSplitOptions.TrimEntries);
            var shortName = parts.Length >= 2 ? $"{parts[0]}, {parts[1]}" : name;
            return new Place(shortName, lat, lon);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Geocode failed for {Query}", query);
            return null;
        }
        finally
        {
            _rateGate.Release();
        }
    }

    internal static bool TryParseCoordinates(string query, out Place? place)
    {
        place = null;
        var parts = query.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            return false;
        if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180) return false;
        place = new Place($"{lat:F3}, {lon:F3}", lat, lon);
        return true;
    }

    public void Dispose() => _http.Dispose();
}
