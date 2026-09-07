using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using OpenWSR.Geo;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>
/// A station network refused the user's key — Weather Underground or Ambient Weather.
///
/// Shared by both because the caller does the same thing either way: keep the forecast, lose
/// that network, say which key to fix. Its own type because these keys are credentials and
/// their failures need a message this code controls — an exception built from the request
/// would carry the key with it, into a log file and onto the error bar of a window somebody
/// may be screen-sharing.
/// </summary>
public sealed class StationAuthException(string message) : Exception(message);

/// <summary>
/// Personal weather stations, through the user's own Weather Underground API key.
///
/// This is the one keyless-charter exception in the app and it is deliberate: there is no
/// unauthenticated way to read a neighbour's station — that was probed rather than assumed,
/// and the table of dead ends is in <c>docs/data-sources.md</c>. The key belongs to the user,
/// is entered in Settings, is sent to <c>api.weather.com</c> and nowhere else, and the app
/// works completely without one. It follows the <c>MapTilerKey</c> precedent exactly.
///
/// Weather Underground issues these keys to people who contribute a station of their own, so
/// anyone holding one is already publishing to this network.
/// </summary>
public sealed class PwsClient : IDisposable
{
    private const string Root = "https://api.weather.com";
    private static readonly ILogger Log = Serilog.Log.ForContext<PwsClient>();

    /// <summary>
    /// Metric, so everything lands in the units the rest of the app stores: Celsius, km/h,
    /// hectopascals, millimetres. Asking for "e" here would push a conversion into the
    /// parser and a second spelling of every quantity into the records.
    /// </summary>
    private const string Units = "m";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public PwsClient(string apiKey, string userAgent)
    {
        _apiKey = apiKey;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>Personal stations near a point, nearest first.</summary>
    public async Task<IReadOnlyList<ObservationStation>> NearbyAsync(
        double latDeg, double lonDeg, CancellationToken ct = default)
    {
        var geocode = string.Create(CultureInfo.InvariantCulture, $"{latDeg:F4},{lonDeg:F4}");
        using var json = await GetJsonAsync(
            $"{Root}/v3/location/near?geocode={geocode}&product=pws&format=json", "nearby", ct);
        if (json is null) return [];

        var stations = ParseNearby(json, latDeg, lonDeg);
        Log.Debug("PWS: {Count} personal stations near {Geocode}", stations.Count, geocode);
        return stations;
    }

    public async Task<CurrentConditions?> GetObservationAsync(
        ObservationStation station, CancellationToken ct = default)
    {
        try
        {
            using var json = await GetJsonAsync(
                $"{Root}/v2/pws/observations/current?stationId={Uri.EscapeDataString(station.Id)}"
                + $"&format=json&units={Units}",
                "observation", ct);

            // Null is "nothing to report", which is how a 204 arrives — see GetJsonAsync.
            return json is null ? null : ParseObservation(json, station);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // A station that has stopped reporting. Ordinary — personal stations go offline
            // far more often than an ASOS does — and handled the same way: skip it.
            Log.Debug("PWS {Id} has no current observation", station.Id);
            return null;
        }
    }

    // ---- parsing, static so it can be tested against committed fixtures ----

    /// <summary>
    /// The location service answers in **parallel arrays** — one array per field, indexed
    /// together — rather than an array of objects. A short array is not an error, so every
    /// field is read by index against the shortest one present.
    /// </summary>
    public static IReadOnlyList<ObservationStation> ParseNearby(
        JsonDocument json, double latDeg, double lonDeg)
    {
        if (!json.RootElement.TryGetProperty("location", out var location)
            || location.ValueKind != JsonValueKind.Object) return [];

        var ids = Array(location, "stationId");
        var names = Array(location, "stationName");
        var lats = Array(location, "latitude");
        var lons = Array(location, "longitude");
        var quality = Array(location, "qcStatus");

        int count = Math.Min(ids.Length, Math.Min(lats.Length, lons.Length));
        var stations = new List<ObservationStation>(count);
        for (int i = 0; i < count; i++)
        {
            var id = ids[i].ValueKind == JsonValueKind.String ? ids[i].GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;
            if (lats[i].ValueKind != JsonValueKind.Number || lons[i].ValueKind != JsonValueKind.Number)
                continue;

            double stationLat = lats[i].GetDouble();
            double stationLon = lons[i].GetDouble();

            stations.Add(new ObservationStation(
                id,
                i < names.Length && names[i].ValueKind == JsonValueKind.String
                    ? names[i].GetString() ?? id
                    : id,
                stationLat,
                stationLon,
                // Our own geodesy, not the API's distanceKm, for the same reason the NWS list
                // is re-measured: the number is drawn on screen beside the station.
                GeoMath.DistanceM(latDeg, lonDeg, stationLat, stationLon) / 1000.0,
                (GeoMath.BearingRad(latDeg, lonDeg, stationLat, stationLon) * 180.0 / Math.PI + 360.0) % 360.0)
            {
                Source = StationSource.Personal,
                // 1 passed, 0 not checked, -1 failed — and "not checked" is null rather than
                // true, because the property claims the station passed and an unchecked one
                // has not. Only an explicit failure excludes a station from the auto-pick.
                PassedQualityCheck = i < quality.Length && quality[i].ValueKind == JsonValueKind.Number
                    ? quality[i].GetInt32() switch { > 0 => true, < 0 => false, _ => null }
                    : null,
            });
        }
        stations.Sort((a, b) => a.DistanceKm.CompareTo(b.DistanceKm));
        return stations;
    }

    /// <summary>
    /// One observation. The readings live in a nested <c>metric</c> object; the few that are
    /// unit-free — wind direction, humidity — sit at the top level beside the station's own
    /// identity, so both levels have to be read.
    /// </summary>
    public static CurrentConditions? ParseObservation(JsonDocument json, ObservationStation station)
    {
        if (!json.RootElement.TryGetProperty("observations", out var observations)
            || observations.ValueKind != JsonValueKind.Array
            || observations.GetArrayLength() == 0) return null;

        var o = observations[0];
        if (o.ValueKind != JsonValueKind.Object) return null;

        if (!o.TryGetProperty("obsTimeUtc", out var stamp)
            || stamp.ValueKind != JsonValueKind.String
            || !stamp.TryGetDateTimeOffset(out var observed)) return null;

        var metric = o.TryGetProperty("metric", out var m) && m.ValueKind == JsonValueKind.Object
            ? m
            : default;

        // The neighbourhood is what the owner called the location, and it is far more use on
        // screen than the station's callsign — "Cedar Ridge" against "KOKNORMAN12".
        var neighbourhood = Text(o, "neighborhood");
        var named = neighbourhood.Length > 0 ? station with { Name = neighbourhood } : station;

        double? heatIndex = Number(metric, "heatIndex");
        double? windChill = Number(metric, "windChill");
        double? temperature = Number(metric, "temp");

        return new CurrentConditions(
            named,
            observed.ToUniversalTime(),
            // A personal station reports numbers, never a description — there is no observer
            // and no ceilometer. The page falls back to its own wording.
            Description: "",
            Sky: "",
            temperature,
            Number(metric, "dewpt"),
            Number(o, "humidity"),
            Number(metric, "windSpeed"),
            Number(metric, "windGust"),
            Number(o, "winddir"),
            // Reported in hectopascals; the app stores pascals, as api.weather.gov sends them.
            Number(metric, "pressure") is { } hpa ? hpa * 100.0 : null,
            // No visibility sensor exists on a domestic station. Absent, not zero.
            VisibilityM: null,
            FeelsLike(temperature, heatIndex, windChill));
    }

    /// <summary>
    /// Weather Underground reports heat index and wind chill as the temperature itself
    /// whenever neither applies, rather than omitting them — so taken at face value every
    /// mild day claims a "feels like" that is merely the air temperature restated. Only a
    /// value that actually differs is worth the row.
    /// </summary>
    private static double? FeelsLike(double? temperature, double? heatIndex, double? windChill)
    {
        var candidate = heatIndex ?? windChill;
        if (candidate is null || temperature is null) return candidate;
        return Math.Abs(candidate.Value - temperature.Value) < 0.5 ? null : candidate;
    }

    // ---- transport ----

    /// <summary>
    /// Returns null for <b>204 No Content</b>, which is how Weather Underground answers for a
    /// station with nothing current — not an error status, so <c>EnsureSuccessStatusCode</c>
    /// lets it straight through, and parsing the empty body then threw a <c>JsonException</c>
    /// that escaped the caller's status-code filter entirely. A success with no body is a
    /// result, and this is where it has to be recognised.
    /// </summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, string what, CancellationToken ct)
    {
        // The key goes on here and never reaches a log line or an exception message. Anything
        // that formats this URL formats the credential with it.
        var response = await _http.GetAsync($"{url}&apiKey={Uri.EscapeDataString(_apiKey)}", ct);
        try
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new StationAuthException(await AuthMessageAsync(response, ct));
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.NoContent) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            // A zero-length body on a 200 gets the same treatment as a 204: it is not JSON,
            // and it is not worth an exception either.
            if (stream.CanSeek && stream.Length == 0) return null;
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (HttpRequestException e)
        {
            // Rethrown with a message this code owns rather than one built from the request,
            // and logged without the URL for the same reason.
            Log.Debug("PWS {What} failed: {Status}", what, e.StatusCode);
            throw;
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Weather Underground distinguishes a missing key from a rejected one in the body —
    /// <c>CDN-0004</c> against <c>CDN-0001</c> — and the two need different advice: one is a
    /// bug in this app, the other is a typo in Settings.
    /// </summary>
    private static async Task<string> AuthMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = "";
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("error", out var error))
            {
                code = Text(error, "code");
            }
        }
        catch (JsonException)
        {
            // An auth failure that does not explain itself still gets the general advice.
        }

        return code == "CDN-0004"
            ? "Weather Underground did not receive a key. Add one under PERSONAL WEATHER "
              + "STATIONS in Settings, or clear the box to go back to NWS stations only."
            // Expiry is named first because it is the likeliest cause by far: these keys have
            // a limited life, so the common case is not a typo but a key that worked for
            // months and has now lapsed. Sending someone to re-check characters they pasted
            // correctly is the wrong first move.
            : "Weather Underground rejected the key. These keys expire, so if it was working "
              + "before, regenerate it at wunderground.com/member/api-keys and paste the new "
              + "one under PERSONAL WEATHER STATIONS in Settings. Clearing the box goes back "
              + "to NWS stations only. The forecast itself is unaffected either way.";
    }

    // ---- json helpers ----

    private static JsonElement[] Array(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()]
            : [];

    private static double? Number(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    private static string Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public void Dispose() => _http.Dispose();
}
