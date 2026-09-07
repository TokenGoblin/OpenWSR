using System.Net;
using System.Net.Http;
using System.Text.Json;
using OpenWSR.Geo;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>
/// The user's own weather station, through the Ambient Weather Network.
///
/// <para>This is a different question from <see cref="PwsClient"/> and the API reflects it.
/// Weather Underground answers "what stations are near this point"; Ambient answers "what
/// stations does this account own" and has **no geolocation endpoint at all**. So this reads
/// your station, not a neighbour's — which is the better reading anyway, because it is the
/// actual ground the forecast is for and you know whether it sits in the sun.</para>
///
/// <para>One request does both jobs: <c>/v1/devices</c> returns each device together with its
/// <c>lastData</c>, so there is no separate observation call and nothing to walk.</para>
///
/// <para>Two keys, both self-serve from the account page: an <b>application key</b>
/// identifying the program and an <b>API key</b> granting access to that user's devices. Both
/// belong to the user. The application key is deliberately *not* baked into this build — the
/// repository is public, so a key committed here would be a key published here, and Ambient
/// issues them per developer for rate-limiting.</para>
/// </summary>
public sealed class AmbientClient : IDisposable
{
    private const string Root = "https://rt.ambientweather.net";
    private static readonly ILogger Log = Serilog.Log.ForContext<AmbientClient>();

    private readonly HttpClient _http;
    private readonly string _applicationKey;
    private readonly string _apiKey;

    public AmbientClient(string applicationKey, string apiKey, string userAgent)
    {
        _applicationKey = applicationKey;
        _apiKey = apiKey;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>
    /// Every station on the account, each with its latest reading.
    ///
    /// <paramref name="fallbackLatDeg"/> and <paramref name="fallbackLonDeg"/> stand in when a
    /// device does not publish coordinates — the REST response carries them only sometimes,
    /// and a station that does not say where it is, is at the place being forecast for, which
    /// is the only assumption available and the right one for a device you own.
    /// </summary>
    public async Task<IReadOnlyList<CurrentConditions>> GetDevicesAsync(
        double fallbackLatDeg, double fallbackLonDeg, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync($"{Root}/v1/devices", ct);
        var devices = ParseDevices(json, fallbackLatDeg, fallbackLonDeg);
        Log.Debug("Ambient: {Count} device(s) on the account", devices.Count);
        return devices;
    }

    /// <summary>
    /// One device by its MAC address, or null if the account no longer has it.
    ///
    /// It re-reads the whole account because that is the only shape the API offers — there is
    /// no per-device current endpoint — but a caller needing exactly one station should not
    /// have to know that, and should certainly not be tempted to route a MAC address at a
    /// service that has never heard of it.
    /// </summary>
    public async Task<CurrentConditions?> GetDeviceAsync(
        ObservationStation station, CancellationToken ct = default)
    {
        var devices = await GetDevicesAsync(station.LatDeg, station.LonDeg, ct);
        return devices.FirstOrDefault(d => d.Station.Id == station.Id);
    }

    // ---- parsing, static so it can be tested against committed fixtures ----

    /// <summary>
    /// <c>/v1/devices</c> answers with an array of devices, each carrying <c>macAddress</c>,
    /// an <c>info</c> block and <c>lastData</c>.
    ///
    /// Everything in <c>lastData</c> is **imperial** — °F, mph, inHg, inches — with no way to
    /// ask for anything else, unlike Weather Underground's <c>units=m</c>. It is converted
    /// here so the rest of the app keeps one spelling of every quantity.
    /// </summary>
    public static IReadOnlyList<CurrentConditions> ParseDevices(
        JsonDocument json, double fallbackLatDeg, double fallbackLonDeg)
    {
        if (json.RootElement.ValueKind != JsonValueKind.Array) return [];

        var devices = new List<CurrentConditions>();
        foreach (var device in json.RootElement.EnumerateArray())
        {
            if (device.ValueKind != JsonValueKind.Object) continue;

            var mac = Text(device, "macAddress");
            if (mac.Length == 0) continue;

            var info = Object(device, "info");
            var (latDeg, lonDeg) = Coordinates(info) ?? (fallbackLatDeg, fallbackLonDeg);

            var name = Text(info, "name");
            if (name.Length == 0) name = Text(info, "location");
            if (name.Length == 0) name = "My weather station";

            var station = new ObservationStation(
                mac,
                name,
                latDeg,
                lonDeg,
                GeoMath.DistanceM(fallbackLatDeg, fallbackLonDeg, latDeg, lonDeg) / 1000.0,
                (GeoMath.BearingRad(fallbackLatDeg, fallbackLonDeg, latDeg, lonDeg) * 180.0 / Math.PI + 360.0) % 360.0)
            {
                Source = StationSource.Own,
            };

            if (Reading(Object(device, "lastData"), station) is { } reading)
                devices.Add(reading);
        }
        return devices;
    }

    private static CurrentConditions? Reading(JsonElement last, ObservationStation station)
    {
        if (last.ValueKind != JsonValueKind.Object) return null;

        // Milliseconds since the epoch. A device that has never reported has no dateutc, and
        // an observation with no time cannot be aged, so it is not an observation.
        if (Number(last, "dateutc") is not { } epochMs) return null;

        double? temperature = Fahrenheit(last, "tempf");

        return new CurrentConditions(
            station,
            DateTimeOffset.FromUnixTimeMilliseconds((long)epochMs),
            // No observer and no ceilometer, exactly as with a Weather Underground station.
            Description: "",
            Sky: "",
            temperature,
            Fahrenheit(last, "dewPoint"),
            Number(last, "humidity"),
            MilesPerHour(last, "windspeedmph"),
            MilesPerHour(last, "windgustmph"),
            Number(last, "winddir"),
            // Relative pressure is the sea-level-corrected figure, which is what a weather
            // report means by pressure; absolute is the raw sensor reading and is only a
            // fallback. Inches of mercury either way.
            InchesOfMercury(last, "baromrelin") ?? InchesOfMercury(last, "baromabsin"),
            VisibilityM: null,
            FeelsLike(temperature, Fahrenheit(last, "feelsLike")))
        {
            // Inches per hour to millimetres. The one reading here that no official station
            // nearby can give you: whether it is raining on your own roof.
            PrecipitationLastHourMm = Number(last, "hourlyrainin") is { } inches
                ? inches * 25.4
                : null,
        };
    }

    /// <summary>
    /// Ambient computes <c>feelsLike</c> as wind chill below 50 °F and heat index above 68 °F,
    /// and simply repeats the air temperature between those — so taken at face value every
    /// mild day claims a "feels like" that says nothing. Same rule as the Weather Underground
    /// path, for the same reason.
    /// </summary>
    private static double? FeelsLike(double? temperatureC, double? feelsLikeC)
    {
        if (feelsLikeC is null || temperatureC is null) return feelsLikeC;
        return Math.Abs(feelsLikeC.Value - temperatureC.Value) < 0.5 ? null : feelsLikeC;
    }

    /// <summary>
    /// <c>info.coords.coords.lat/lon</c> when the device publishes it. The REST response
    /// carries this only sometimes — the published example omits it entirely while real
    /// accounts include it — so every level is probed rather than assumed.
    /// </summary>
    private static (double LatDeg, double LonDeg)? Coordinates(JsonElement info)
    {
        var coords = Object(Object(info, "coords"), "coords");
        if (Number(coords, "lat") is { } lat && Number(coords, "lon") is { } lon)
            return (lat, lon);
        return null;
    }

    // ---- transport ----

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        // Both keys go on here and neither reaches a log line or an exception message.
        var response = await _http.GetAsync(
            $"{url}?applicationKey={Uri.EscapeDataString(_applicationKey)}"
            + $"&apiKey={Uri.EscapeDataString(_apiKey)}", ct);
        try
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new StationAuthException(await AuthMessageAsync(response, ct));

            // 429 is its own advice: the account is over its budget, not misconfigured, and
            // there is nothing for the user to fix except wait.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new StationAuthException(
                    "Ambient Weather is rate-limiting this account — it allows one request a "
                    + "second per API key. The forecast page refreshes every ten minutes, so "
                    + "this usually means something else is using the same key. It will clear "
                    + "on its own.");
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (HttpRequestException e)
        {
            // Logged without the URL, which carries both keys.
            Log.Debug("Ambient devices request failed: {Status}", e.StatusCode);
            throw;
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Ambient names which key is at fault in the body — <c>apiKey-missing</c>,
    /// <c>applicationKey-invalid</c> and so on — and with two keys in play that distinction is
    /// the difference between a useful message and "something is wrong with one of your keys".
    /// </summary>
    private static async Task<string> AuthMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var error = "";
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            error = Text(json.RootElement, "error");
        }
        catch (JsonException)
        {
            // A refusal that does not explain itself still gets the general advice.
        }

        var which = error.StartsWith("applicationKey", StringComparison.OrdinalIgnoreCase)
            ? "application key"
            : error.StartsWith("apiKey", StringComparison.OrdinalIgnoreCase)
                ? "API key"
                : "keys";

        return $"Ambient Weather did not accept the {which}. Both are created on the same page "
             + "— ambientweather.net/account — and they are different things: the API key grants "
             + "access to your devices, the application key identifies the program asking. "
             + "Check them under MY WEATHER STATION in Settings, or clear them to go back to "
             + "official stations. The forecast itself is unaffected either way.";
    }

    // ---- json helpers, and the unit conversions Ambient forces ----

    private static double? Fahrenheit(JsonElement parent, string name) =>
        Number(parent, name) is { } f ? (f - 32.0) * 5.0 / 9.0 : null;

    private static double? MilesPerHour(JsonElement parent, string name) =>
        Number(parent, name) is { } mph ? mph * 1.609344 : null;

    private static double? InchesOfMercury(JsonElement parent, string name) =>
        Number(parent, name) is { } inHg ? inHg * 3386.388640341 : null;

    private static JsonElement Object(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

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
