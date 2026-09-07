using System.IO;
using System.Text.Json;
using OpenWSR.Ingest;

namespace OpenWSR.App.Tests;

/// <summary>
/// The forecast page reads api.weather.gov, and every field it draws is optional in the feed.
/// The fixtures are live captures kept verbatim in <c>assets/testdata/forecast/</c>: a Norman,
/// OK gridpoint (the same ground as the Level II golden volumes), the 55 stations NWS lists
/// for it, and two observations chosen for what they are *missing* — KOUN reports a
/// temperature and no icon, description, dew point or pressure at all.
/// </summary>
public class ForecastClientTests
{
    private static JsonDocument Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenWSR.slnx")))
            dir = dir.Parent;
        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir!.FullName, "assets", "testdata", "forecast", name)));
    }

    // The point the fixtures were captured for: Norman, OK.
    private const double LatDeg = 35.2226;
    private const double LonDeg = -97.4395;

    [Fact]
    public void PointCarriesTheGridAndTheTown()
    {
        using var json = Fixture("points.json");
        var point = ForecastClient.ParsePoint(json);

        Assert.Equal("OUN", point.Office);
        Assert.Equal("Norman, OK", point.PlaceName);
        Assert.Equal("America/Chicago", point.TimeZone);
        Assert.Equal("https://api.weather.gov/gridpoints/OUN/100,83/forecast", point.ForecastUrl);
        Assert.Equal("https://api.weather.gov/gridpoints/OUN/100,83/stations", point.StationsUrl);
    }

    [Fact]
    public void ForecastIsSevenDaysOfHalfDayPeriods()
    {
        using var json = Fixture("forecast.json");
        var (generatedAt, periods) = ForecastClient.ParseForecast(json);

        Assert.Equal(14, periods.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 2, 12, 8, TimeSpan.Zero), generatedAt);

        var tonight = periods[0];
        Assert.Equal(1, tonight.Number);
        Assert.Equal("Tonight", tonight.Name);
        Assert.False(tonight.IsDaytime);
        Assert.Equal(2, tonight.PrecipitationChancePercent);
        Assert.Equal("few", tonight.Sky);
        Assert.Equal("Mostly Clear", tonight.ShortForecast);
        Assert.Equal("S", tonight.WindDirection);
        Assert.Equal("6 mph", tonight.WindSpeed);
        Assert.StartsWith("Mostly clear, with a low around 77.", tonight.DetailedForecast);
    }

    /// <summary>
    /// The API reports Fahrenheit from every US office. Storing the raw number and its unit
    /// would push that branch into every caller; storing Celsius means <c>Units</c> decides
    /// how it reads, the same as every other quantity in the app.
    /// </summary>
    [Fact]
    public void FahrenheitIsNormalisedToCelsius()
    {
        using var json = Fixture("forecast.json");
        var (_, periods) = ForecastClient.ParseForecast(json);

        Assert.Equal(25.0, periods[0].TemperatureC, 6);          // 77 °F, "Tonight"
        Assert.Equal(37.22222222222222, periods[1].TemperatureC, 6);  // 99 °F, "Labor Day"
    }

    /// <summary>
    /// Nearest first, by our own geodesy — the distance is drawn beside each station in the
    /// picker, so it has to be a number this app computed rather than an order the API
    /// happened to return in.
    /// </summary>
    [Fact]
    public void StationsAreSortedNearestFirst()
    {
        using var json = Fixture("stations.json");
        var stations = ForecastClient.ParseStations(json, LatDeg, LonDeg);

        Assert.Equal(55, stations.Count);
        Assert.Equal("KOUN", stations[0].Id);
        Assert.Equal("Norman / Max Westheimer", stations[0].Name);
        Assert.Equal(3.672, stations[0].DistanceKm, 2);
        Assert.Equal(309.3, stations[0].BearingDeg, 1);

        Assert.Equal("KTIK", stations[1].Id);
        Assert.Equal(22.17, stations[1].DistanceKm, 2);

        for (int i = 1; i < stations.Count; i++)
            Assert.True(stations[i].DistanceKm >= stations[i - 1].DistanceKm);
    }

    /// <summary>Atan2 is negative west of north; a compass point wants 0–360.</summary>
    [Fact]
    public void BearingsAreAllPositive()
    {
        using var json = Fixture("stations.json");
        foreach (var station in ForecastClient.ParseStations(json, LatDeg, LonDeg))
            Assert.InRange(station.BearingDeg, 0.0, 360.0);
    }

    [Fact]
    public void ObservationReadsTheFieldsAStationDidReport()
    {
        using var json = Fixture("observation-KBOS.json");
        var current = ForecastClient.ParseObservation(json, Station("KBOS"));

        Assert.NotNull(current);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 1, 40, 0, TimeSpan.Zero), current!.ObservedUtc);
        Assert.Equal("Clear", current.Description);
        Assert.Equal("skc", current.Sky);
        Assert.Equal(18.0, current.TemperatureC);
        Assert.Equal(12.96, current.WindSpeedKmh);
        Assert.Equal(300.0, current.WindDirectionDeg);
        Assert.Equal(101523.93, current.PressurePa);
        Assert.Equal(16093.44, current.VisibilityM);
    }

    /// <summary>
    /// A missing reading is the normal case, not a parse failure. NWS sends the measurement
    /// object with <c>"value": null</c> and <c>qualityControl: "Z"</c>, so the presence of
    /// the property proves nothing — and KOUN in this capture reports a temperature and
    /// essentially nothing else, with no icon and an empty description.
    /// </summary>
    [Fact]
    public void MissingReadingsComeBackNullRatherThanZero()
    {
        using var json = Fixture("observation-KOUN.json");
        var current = ForecastClient.ParseObservation(json, Station("KOUN"));

        Assert.NotNull(current);
        Assert.Equal(36.0, current!.TemperatureC);
        Assert.Null(current.DewpointC);
        Assert.Null(current.RelativeHumidityPercent);
        Assert.Null(current.WindGustKmh);
        Assert.Null(current.PressurePa);       // both barometric and sea-level are null
        Assert.Null(current.FeelsLikeC);       // neither heat index nor wind chill
        Assert.Equal("", current.Description);
        Assert.Equal("", current.Sky);         // icon is null in this record
    }

    [Theory]
    // The plain case, and the one that matters: a split period carries two conditions and a
    // rain chance, and only the leading token describes the period as a whole.
    [InlineData("https://api.weather.gov/icons/land/night/few?size=medium", "few")]
    [InlineData("https://api.weather.gov/icons/land/day/hot?size=medium", "hot")]
    [InlineData("https://api.weather.gov/icons/land/night/tsra_sct,50/tsra_sct,30?size=medium", "tsra_sct")]
    [InlineData("https://api.weather.gov/icons/land/day/tsra_hi,20/sct?size=medium", "tsra_hi")]
    [InlineData("https://api.weather.gov/icons/land/night/wind_few?size=medium", "wind_few")]
    // Nothing to read: no icon at all, and a URL with no day/night segment to anchor on.
    [InlineData("", "")]
    [InlineData("https://api.weather.gov/icons/land", "")]
    public void SkyTokenIsTheConditionWordOutOfTheIconUrl(string icon, string expected) =>
        Assert.Equal(expected, ForecastClient.SkyToken(icon));

    /// <summary>
    /// "land" contains "and" and the set path has segments of its own; only a segment that
    /// *is* "day" or "night" anchors the condition that follows it.
    /// </summary>
    [Fact]
    public void SkyTokenIsNotFooledByThePathAroundIt()
    {
        Assert.Equal("skc", ForecastClient.SkyToken("https://api.weather.gov/icons/land/day/skc"));
        Assert.True(ForecastClient.IsNightIcon("https://api.weather.gov/icons/land/night/skc"));
        Assert.False(ForecastClient.IsNightIcon("https://api.weather.gov/icons/land/day/skc"));
    }

    [Fact]
    public void PeriodsGroupIntoCalendarDays()
    {
        using var json = Fixture("forecast.json");
        var (_, periods) = ForecastClient.ParseForecast(json);
        var days = ForecastClient.GroupIntoDays(periods);

        Assert.Equal(8, days.Count);

        // Captured in the evening, so the first day has only its night half — and the last
        // has only its day half, because the feed runs out mid-week.
        var first = days[0];
        Assert.Equal(new DateOnly(2026, 9, 6), first.Date);
        Assert.Equal("Tonight", first.Label);
        Assert.Null(first.Day);
        Assert.NotNull(first.Night);
        Assert.Null(first.HighC);
        Assert.Equal(25.0, first.LowC!.Value, 6);

        var labourDay = days[1];
        Assert.Equal("Labor Day", labourDay.Label);   // the office's own wording, holidays and all
        Assert.NotNull(labourDay.Day);
        Assert.NotNull(labourDay.Night);
        Assert.Equal(37.22222222222222, labourDay.HighC!.Value, 6);
        Assert.Equal(25.0, labourDay.LowC!.Value, 6);

        var last = days[^1];
        Assert.Equal("Sunday", last.Label);
        Assert.NotNull(last.Day);
        Assert.Null(last.Night);
        Assert.Null(last.LowC);
    }

    /// <summary>
    /// A day whose daytime half has passed is still that day, not "Tuesday Night" — the row
    /// is headed by the day, and the halves are named inside it.
    /// </summary>
    [Fact]
    public void ANightOnlyDayIsLabelledByTheDay()
    {
        var night = Period("Tuesday Night", new DateTimeOffset(2026, 9, 8, 18, 0, 0, TimeSpan.FromHours(-5)));
        var days = ForecastClient.GroupIntoDays([night]);

        Assert.Single(days);
        Assert.Equal("Tuesday", days[0].Label);
        Assert.Equal(new DateOnly(2026, 9, 8), days[0].Date);
    }

    /// <summary>
    /// The grouping uses each period's own offset. Converting to the machine's local time
    /// would file an evening period on the west coast under the following day for anyone
    /// watching from further east — and looking at somebody else's weather is exactly what
    /// the search box is for.
    /// </summary>
    [Fact]
    public void GroupingUsesTheForecastsOwnTimeZone()
    {
        // 8pm Pacific on the 8th. In UTC that is already the 9th.
        var evening = Period(
            "Tuesday Night", new DateTimeOffset(2026, 9, 8, 20, 0, 0, TimeSpan.FromHours(-7)));

        var days = ForecastClient.GroupIntoDays([evening]);
        Assert.Equal(new DateOnly(2026, 9, 8), days[0].Date);
    }

    /// <summary>
    /// A station answering 200 with a null body is skipped, not thrown over. TryGetProperty
    /// answers true for a JSON null and reading a property off one throws, so the whole
    /// forecast load used to abort on a case that the 404 path already handles gracefully.
    /// </summary>
    [Theory]
    [InlineData("""{"properties": null}""")]
    [InlineData("""{"properties": []}""")]
    [InlineData("""{}""")]
    [InlineData("""{"properties": {"timestamp": null}}""")]
    public void AnEmptyObservationBodyIsSkippedRatherThanThrown(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Null(ForecastClient.ParseObservation(json, Station("KXXX")));
    }

    /// <summary>
    /// One malformed feature costs that station, not the whole list. The feed is already not
    /// trusted to carry a geometry; it is not trusted to carry coordinates either.
    /// </summary>
    [Fact]
    public void AMalformedStationIsSkippedAndTheRestSurvive()
    {
        using var json = JsonDocument.Parse("""
        {"features": [
          {"geometry": {"type": "Point"},
           "properties": {"stationIdentifier": "KNOGEOM", "name": "No coordinates"}},
          {"geometry": {"type": "Point", "coordinates": []},
           "properties": {"stationIdentifier": "KEMPTY", "name": "Empty coordinates"}},
          {"geometry": {"type": "Point", "coordinates": [-97.47, 35.24]},
           "properties": null},
          {"geometry": {"type": "Point", "coordinates": [-97.4708, 35.2435]},
           "properties": {"stationIdentifier": "KOUN", "name": "Norman"}}
        ]}
        """);

        var stations = ForecastClient.ParseStations(json, LatDeg, LonDeg);

        Assert.Single(stations);
        Assert.Equal("KOUN", stations[0].Id);
    }

    /// <summary>
    /// The array guard has to reach the elements, not just the length. A feature carrying
    /// <c>"coordinates": [null, null]</c> clears every check on the array itself and then
    /// throws on <c>GetDouble</c> — aborting the whole load over one station, which is the
    /// thing the guard exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("[null, null]")]
    [InlineData("[\"-97.47\", \"35.24\"]")]
    [InlineData("[-97.47]")]
    [InlineData("{}")]
    public void AStationWithUnreadableCoordinatesIsSkipped(string coordinates)
    {
        using var json = JsonDocument.Parse(
            "{\"features\": ["
            + "{\"geometry\": {\"type\": \"Point\", \"coordinates\": " + coordinates + "},"
            + " \"properties\": {\"stationIdentifier\": \"KBAD\", \"name\": \"Bad\"}},"
            + "{\"geometry\": {\"type\": \"Point\", \"coordinates\": [-97.4708, 35.2435]},"
            + " \"properties\": {\"stationIdentifier\": \"KOUN\", \"name\": \"Norman\"}}"
            + "]}");

        var stations = ForecastClient.ParseStations(json, LatDeg, LonDeg);

        Assert.Single(stations);
        Assert.Equal("KOUN", stations[0].Id);
    }

    private static ObservationStation Station(string id) =>
        new(id, id, LatDeg, LonDeg, 0, 0);

    private static ForecastPeriod Period(string name, DateTimeOffset start) =>
        new(1, name, start, start.AddHours(12), IsDaytime: false, 20, null, "", "", "few", "", "");
}
