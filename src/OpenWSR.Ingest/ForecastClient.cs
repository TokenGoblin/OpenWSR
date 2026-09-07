using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using OpenWSR.Geo;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>
/// One half-day of the NWS forecast. Temperatures are Celsius so <c>Units</c> can render
/// them: the API reports Fahrenheit from US offices and Celsius from others, and a display
/// layer that has to ask which is a display layer that will one day forget to.
/// </summary>
public sealed record ForecastPeriod(
    int Number,
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsDaytime,
    double TemperatureC,
    int? PrecipitationChancePercent,
    string WindSpeed,
    string WindDirection,
    string Sky,
    string ShortForecast,
    string DetailedForecast);

/// <summary>
/// A calendar day as the page draws it: the daytime period and the night that follows it.
/// Either half can be missing — the first day is usually night-only, because a forecast
/// fetched in the evening starts at "Tonight".
/// </summary>
public sealed record ForecastDay(DateOnly Date, string Label, ForecastPeriod? Day, ForecastPeriod? Night)
{
    /// <summary>The high, or null when this day's daytime period has already passed.</summary>
    public double? HighC => Day?.TemperatureC;

    public double? LowC => Night?.TemperatureC;

    /// <summary>The daytime sky where there is one, so a row reads as the day it describes.</summary>
    public ForecastPeriod? Face => Day ?? Night;
}

/// <summary>
/// Who runs the instrument, which is a claim about the reading rather than about the feed.
/// An NWS station is calibrated and maintained on a schedule; a personal one is somebody's
/// garden, is frequently far closer, and has no guarantee behind it at all. The page names
/// which, because that difference is the whole reason to offer both.
/// </summary>
public enum StationSource
{
    /// <summary>An NWS-carried station: ASOS, AWOS, RWIS or another mesonet feed.</summary>
    Nws,

    /// <summary>Somebody else's personal station, via the user's Weather Underground key.</summary>
    Personal,

    /// <summary>
    /// The user's *own* station, via their Ambient Weather keys. Kept apart from
    /// <see cref="Personal"/> because the claim is different in kind: a neighbour's station is
    /// merely nearer, while this one is the actual ground the forecast is for, and its owner
    /// knows whether it sits in the sun or under a tree. It always wins the automatic pick.
    /// </summary>
    Own,
}

/// <summary>
/// <see cref="Source"/> is an init-only property rather than a positional parameter so that
/// adding it did not rewrite every construction site, and because NWS is the right default
/// for anything that does not say otherwise.
/// </summary>
public sealed record ObservationStation(
    string Id,
    string Name,
    double LatDeg,
    double LonDeg,
    double DistanceKm,
    double BearingDeg)
{
    public StationSource Source { get; init; } = StationSource.Nws;

    /// <summary>
    /// Whether the station passed its network's quality check. Null where the network does
    /// not publish one, which is every NWS station here — absent is not the same as failed,
    /// and a station with nothing to say about its own quality must not sort below one that
    /// has explicitly failed.
    /// </summary>
    public bool? PassedQualityCheck { get; init; }
}

/// <summary>
/// What one station is reporting right now. Every field but the station and the timestamp is
/// nullable and most of them are null much of the time — an ASOS drops humidity and pressure
/// out of individual cycles, and a mesonet station may report temperature and nothing else.
/// A missing value is a value: it must draw as an em dash rather than as zero.
/// </summary>
public sealed record CurrentConditions(
    ObservationStation Station,
    DateTimeOffset ObservedUtc,
    string Description,
    string Sky,
    double? TemperatureC,
    double? DewpointC,
    double? RelativeHumidityPercent,
    double? WindSpeedKmh,
    double? WindGustKmh,
    double? WindDirectionDeg,
    double? PressurePa,
    double? VisibilityM,
    double? FeelsLikeC)
{
    public TimeSpan Age => DateTimeOffset.UtcNow - ObservedUtc;

    /// <summary>
    /// Rain in the last hour, millimetres. Init-only so adding it did not rewrite every
    /// construction site.
    ///
    /// <para>Only a home station fills this in. api.weather.gov carries
    /// <c>precipitationLastHour</c> but every observation captured for the fixtures reports it
    /// null, so the unit its <c>unitCode</c> declares has never been seen — and a rainfall
    /// figure wrong by a factor of a thousand is worse than an absent one. Wire the NWS side up
    /// against a live non-null sample, not against the documentation.</para>
    /// </summary>
    public double? PrecipitationLastHourMm { get; init; }
}

/// <summary>Everything the forecast page draws for one point on the ground.</summary>
public sealed record LocalForecast(
    string PlaceName,
    string Office,
    string TimeZone,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ForecastPeriod> Periods,
    CurrentConditions? Current,
    IReadOnlyList<ObservationStation> Stations)
{
    public IReadOnlyList<ForecastDay> Days => ForecastClient.GroupIntoDays(Periods);
}

/// <summary>
/// The point asked about is outside the area the National Weather Service forecasts for.
///
/// Its own class because it is not a failure and must not read as one: <c>/points</c> answers
/// 404 for the Gulf, the Pacific, Canada and Mexico, all of which are an ordinary pan away
/// with no place saved. Reported as a network problem it sends someone to check their wifi
/// over a question that has no answer.
/// </summary>
public sealed class OutsideForecastAreaException(string message) : Exception(message);

/// <summary>Where <c>/points</c> says to look for everything else.</summary>
public sealed record ForecastPoint(
    string ForecastUrl,
    string StationsUrl,
    string Office,
    string PlaceName,
    string TimeZone);

/// <summary>
/// api.weather.gov forecast and surface observations — the same host, User-Agent rule and
/// keyless terms as <see cref="AlertsClient"/>.
///
/// The observation half exists because "what is it doing outside" and "what does the model
/// say" are different questions, and a nearby station is the closest keyless answer to the
/// first. The station list is deliberately not filtered to airports: NWS carries RWIS and
/// other mesonet feeds in the same list, and one of those is frequently nearer than the ASOS.
/// </summary>
public sealed class ForecastClient : IDisposable
{
    private const string Root = "https://api.weather.gov";
    private static readonly ILogger Log = Serilog.Log.ForContext<ForecastClient>();

    /// <summary>
    /// How many of the nearest stations to try before giving up. Stations go quiet — a
    /// mesonet site loses power, an ASOS drops a cycle — and a nearest-only rule would then
    /// report nothing at all while a station eight miles further on was reporting fine.
    /// </summary>
    private const int StationsToTry = 6;

    /// <summary>
    /// Older than this and an observation is not "current conditions" any more. Two hours is
    /// deliberately generous: many mesonet stations report hourly and the API's own ingest
    /// runs a few minutes behind, so a tighter bound rejects stations that are working.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    private readonly HttpClient _http;

    /// <summary>
    /// <c>/points</c> is a grid lookup that does not change, so it is cached for the session.
    /// It costs a round trip before either of the two requests that carry the actual answer.
    /// </summary>
    private readonly Dictionary<string, ForecastPoint> _points = [];

    public ForecastClient(string userAgent)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");
    }

    /// <summary>
    /// Fetch the forecast and the nearest usable observation for a point.
    /// <paramref name="preferredStationId"/> pins a station the user picked; when it is null,
    /// or has gone quiet, the nearest station with a recent observation wins.
    /// </summary>
    public async Task<LocalForecast> GetAsync(
        double latDeg, double lonDeg, string? preferredStationId = null, CancellationToken ct = default)
    {
        var point = await GetPointAsync(latDeg, lonDeg, ct);

        using var forecastJson = await GetJsonAsync(point.ForecastUrl, ct);
        var (generatedAt, periods) = ParseForecast(forecastJson);

        using var stationsJson = await GetJsonAsync(point.StationsUrl, ct);
        var stations = ParseStations(stationsJson, latDeg, lonDeg);

        var current = await GetCurrentAsync(stations, preferredStationId, ct);

        Log.Debug(
            "Forecast for {Place}: {Periods} periods, {Stations} stations, observing from {Station}",
            point.PlaceName, periods.Count, stations.Count, current?.Station.Id ?? "(none)");

        return new LocalForecast(
            point.PlaceName, point.Office, point.TimeZone, generatedAt, periods, current, stations);
    }

    /// <summary>
    /// Re-read one station without re-fetching the forecast — what the station picker needs.
    /// A station the user chose explicitly is returned even when its observation is stale;
    /// the page says how old it is, and silently substituting a different station would make
    /// the picker look broken.
    /// </summary>
    public async Task<CurrentConditions?> GetObservationAsync(
        ObservationStation station, CancellationToken ct = default)
    {
        try
        {
            using var json = await GetJsonAsync($"{Root}/stations/{station.Id}/observations/latest", ct);
            return ParseObservation(json, station);
        }
        catch (HttpRequestException e)
        {
            // A station with nothing current answers 404, which is an ordinary state of the
            // world here rather than a failure worth putting in front of anyone.
            Log.Debug("Station {Id} has no current observation: {Message}", station.Id, e.Message);
            return null;
        }
    }

    private async Task<CurrentConditions?> GetCurrentAsync(
        IReadOnlyList<ObservationStation> stations, string? preferredStationId, CancellationToken ct)
    {
        if (preferredStationId is not null
            && stations.FirstOrDefault(s => s.Id == preferredStationId) is { } pinned)
        {
            var chosen = await GetObservationAsync(pinned, ct);
            if (chosen is not null) return chosen;
        }

        foreach (var station in stations.Take(StationsToTry))
        {
            var observation = await GetObservationAsync(station, ct);
            // A station reporting no temperature at all is reporting nothing worth showing,
            // whatever else came back with it.
            if (observation is { TemperatureC: not null } && observation.Age < StaleAfter)
                return observation;
        }
        return null;
    }

    private async Task<ForecastPoint> GetPointAsync(double latDeg, double lonDeg, CancellationToken ct)
    {
        // Four decimals is about 11 m — far finer than the 2.5 km forecast grid, and enough
        // that returning to the same place does not re-ask for the same cell.
        var key = string.Create(CultureInfo.InvariantCulture, $"{latDeg:F4},{lonDeg:F4}");
        if (_points.TryGetValue(key, out var cached)) return cached;

        JsonDocument json;
        try
        {
            json = await GetJsonAsync($"{Root}/points/{key}", ct);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Not a failure: the NWS forecasts for the US and its territories, and this point
            // is not in one. Distinguished here rather than at the top because only /points
            // answers this question — a 404 from the forecast or a station means something
            // else entirely.
            throw new OutsideForecastAreaException(
                "The National Weather Service does not forecast for this location. It covers "
                + "the United States and its territories, so a point out at sea or over "
                + "Canada or Mexico has no forecast to fetch.");
        }

        using (json)
        {
            var point = ParsePoint(json);
            _points[key] = point;
            return point;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    // ---- parsing, kept static so it can be tested against the committed fixtures ----

    public static ForecastPoint ParsePoint(JsonDocument json)
    {
        var p = json.RootElement.GetProperty("properties");

        var place = "";
        if (p.TryGetProperty("relativeLocation", out var relative)
            && relative.TryGetProperty("properties", out var rp))
        {
            var city = Text(rp, "city");
            var state = Text(rp, "state");
            place = state.Length > 0 && city.Length > 0 ? $"{city}, {state}" : city;
        }

        return new ForecastPoint(
            Text(p, "forecast"),
            Text(p, "observationStations"),
            Text(p, "gridId"),
            place,
            Text(p, "timeZone"));
    }

    public static (DateTimeOffset GeneratedAt, IReadOnlyList<ForecastPeriod> Periods) ParseForecast(
        JsonDocument json)
    {
        var p = json.RootElement.GetProperty("properties");
        var generated = p.TryGetProperty("generatedAt", out var g) && g.TryGetDateTimeOffset(out var when)
            ? when
            : DateTimeOffset.UtcNow;

        var periods = new List<ForecastPeriod>();
        foreach (var period in p.GetProperty("periods").EnumerateArray())
        {
            var unit = Text(period, "temperatureUnit");
            double temperature = period.GetProperty("temperature").GetDouble();
            // "F" from every US office, "C" from the rest. Storing the raw number and its
            // unit would push the same branch into every caller that reads it.
            double celsius = unit.Equals("F", StringComparison.OrdinalIgnoreCase)
                ? (temperature - 32) * 5 / 9
                : temperature;

            periods.Add(new ForecastPeriod(
                period.GetProperty("number").GetInt32(),
                Text(period, "name"),
                period.GetProperty("startTime").GetDateTimeOffset(),
                period.GetProperty("endTime").GetDateTimeOffset(),
                period.GetProperty("isDaytime").GetBoolean(),
                celsius,
                Percent(period, "probabilityOfPrecipitation"),
                Text(period, "windSpeed"),
                Text(period, "windDirection"),
                SkyToken(Text(period, "icon")),
                Text(period, "shortForecast"),
                Text(period, "detailedForecast")));
        }
        return (generated, periods);
    }

    /// <summary>
    /// Nearest first, by our own geodesy rather than by the order the API happened to return.
    /// The distance is drawn on screen next to each station, so it has to be a number this
    /// app computed and can stand behind.
    /// </summary>
    public static IReadOnlyList<ObservationStation> ParseStations(
        JsonDocument json, double latDeg, double lonDeg)
    {
        var stations = new List<ObservationStation>();
        foreach (var feature in json.RootElement.GetProperty("features").EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry)
                || geometry.ValueKind != JsonValueKind.Object) continue;

            // Guarded to the same depth as the geometry above it. One malformed feature in a
            // list of fifty-five must cost that station, not the whole forecast — and the
            // guard one line up already says this feed is not trusted to be well-formed.
            if (!geometry.TryGetProperty("coordinates", out var coordinates)
                || coordinates.ValueKind != JsonValueKind.Array
                || coordinates.GetArrayLength() < 2
                // The elements too, not only the array: `"coordinates": [null, null]` clears
                // every check above and then throws on GetDouble, which would abort the whole
                // load over one station — the thing the guard exists to prevent.
                || coordinates[0].ValueKind != JsonValueKind.Number
                || coordinates[1].ValueKind != JsonValueKind.Number) continue;

            double stationLon = coordinates[0].GetDouble();
            double stationLat = coordinates[1].GetDouble();

            if (!feature.TryGetProperty("properties", out var p)
                || p.ValueKind != JsonValueKind.Object) continue;

            var id = Text(p, "stationIdentifier");
            if (id.Length == 0) continue;

            stations.Add(new ObservationStation(
                id,
                Text(p, "name"),
                stationLat,
                stationLon,
                GeoMath.DistanceM(latDeg, lonDeg, stationLat, stationLon) / 1000.0,
                // Atan2 returns ±180°; a compass point wants 0–360.
                (GeoMath.BearingRad(latDeg, lonDeg, stationLat, stationLon) * 180.0 / Math.PI + 360.0) % 360.0));
        }
        stations.Sort((a, b) => a.DistanceKm.CompareTo(b.DistanceKm));
        return stations;
    }

    public static CurrentConditions? ParseObservation(JsonDocument json, ObservationStation station)
    {
        // TryGetProperty answers true for a JSON null, and reading a property off one throws
        // rather than returning false — so a station answering 200 with a null body would
        // abort the whole load instead of being skipped the way a 404 is.
        if (!json.RootElement.TryGetProperty("properties", out var p)
            || p.ValueKind != JsonValueKind.Object) return null;
        // TryGetDateTimeOffset throws rather than answering false when the element is not a
        // string, so a null timestamp needs the same kind check the body above it does.
        if (!p.TryGetProperty("timestamp", out var stamp)
            || stamp.ValueKind != JsonValueKind.String
            || !stamp.TryGetDateTimeOffset(out var observed)) return null;

        double? heatIndex = Measure(p, "heatIndex");
        double? windChill = Measure(p, "windChill");

        return new CurrentConditions(
            station,
            observed.ToUniversalTime(),
            Text(p, "textDescription"),
            SkyToken(Text(p, "icon")),
            Measure(p, "temperature"),
            Measure(p, "dewpoint"),
            Measure(p, "relativeHumidity"),
            Measure(p, "windSpeed"),
            Measure(p, "windGust"),
            Measure(p, "windDirection"),
            Measure(p, "barometricPressure") ?? Measure(p, "seaLevelPressure"),
            Measure(p, "visibility"),
            // Only one of the two is ever reported, and which one depends on the season.
            heatIndex ?? windChill);
    }

    /// <summary>
    /// Group the half-day periods into calendar days in the forecast's own local time.
    ///
    /// The period's own offset is what makes this right: converting to the machine's local
    /// time would file a 6pm Pacific period under the following day for anyone watching from
    /// further east, which is the whole point of being able to look at somebody else's
    /// weather.
    /// </summary>
    public static IReadOnlyList<ForecastDay> GroupIntoDays(IReadOnlyList<ForecastPeriod> periods)
    {
        var days = new List<ForecastDay>();
        foreach (var period in periods)
        {
            // A night period belongs to the day it starts on, which is what "Monday Night"
            // means; its own start is already the evening of that day.
            var date = DateOnly.FromDateTime(period.Start.DateTime);

            int index = days.FindIndex(d => d.Date == date);
            if (index < 0)
            {
                days.Add(new ForecastDay(
                    date,
                    // The API's own wording where it has some — "Tonight", "Labor Day",
                    // "Christmas Day" — because it knows about holidays and we do not.
                    period.IsDaytime ? period.Name : DayLabel(period, date),
                    period.IsDaytime ? period : null,
                    period.IsDaytime ? null : period));
                continue;
            }

            var existing = days[index];
            days[index] = period.IsDaytime
                ? existing with { Day = period, Label = period.Name }
                : existing with { Night = period };
        }
        return days;
    }

    /// <summary>
    /// "Monday Night" names a day whose daytime half has already gone. Trimming the suffix
    /// keeps the row headed by the day rather than by the half of it that is left, except
    /// for tonight, where "Tonight" is the more useful word.
    /// </summary>
    private static string DayLabel(ForecastPeriod night, DateOnly date)
    {
        var name = night.Name;
        if (name.Equals("Tonight", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Overnight", StringComparison.OrdinalIgnoreCase))
            return name;
        if (name.EndsWith(" Night", StringComparison.OrdinalIgnoreCase))
            return name[..^" Night".Length];
        return date.ToString("dddd", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// The condition word out of an icon URL:
    /// <c>.../icons/land/night/tsra_sct,50/tsra_sct,30?size=medium</c> is "tsra_sct".
    ///
    /// The icon is the only place the API states the condition as a token rather than as
    /// prose — <c>shortForecast</c> is a sentence written for a human — so this is what a
    /// glyph can be chosen from. The trailing number is a rain chance and the second path
    /// segment is the back half of a split period; the leading token describes both well
    /// enough for one symbol.
    /// </summary>
    public static string SkyToken(string iconUrl)
    {
        if (iconUrl.Length == 0) return "";

        var path = iconUrl.Split('?')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Everything after "day" or "night" is condition; anything before is the icon set's
        // own path and must not be mistaken for one.
        int start = Array.FindLastIndex(segments, s =>
            s.Equals("day", StringComparison.OrdinalIgnoreCase)
            || s.Equals("night", StringComparison.OrdinalIgnoreCase));
        if (start < 0 || start + 1 >= segments.Length) return "";

        return segments[start + 1].Split(',')[0];
    }

    /// <summary>True when the icon says night, which decides moon against sun.</summary>
    public static bool IsNightIcon(string iconUrl) =>
        iconUrl.Contains("/night/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A NWS measurement object, or null. The value is null far more often than not, and
    /// <c>qualityControl: "Z"</c> — the station did not report it — comes back with the
    /// object still present, so the presence of the property proves nothing.
    /// </summary>
    private static double? Measure(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var measure) || measure.ValueKind != JsonValueKind.Object)
            return null;
        if (!measure.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.GetDouble();
    }

    private static int? Percent(JsonElement parent, string name) =>
        Measure(parent, name) is { } value ? (int)Math.Round(value) : null;

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public void Dispose() => _http.Dispose();
}
