using System.Text.Json;
using OpenWSR.Ingest;

namespace OpenWSR.App.Tests;

/// <summary>
/// Personal weather stations, through the user's own Weather Underground key.
///
/// <para><b>These fixtures are built from Weather Underground's published response shapes,
/// not captured from a live call</b> — unlike every other fixture in this suite, which is a
/// real response committed verbatim. Issuing a key requires contributing a station, so there
/// was none to capture with. The endpoints themselves were verified to exist and to gate on
/// the key (401 <c>CDN-0004 Missing apiKey</c> unkeyed, <c>CDN-0001 Invalid apiKey</c> with a
/// bad one), so the paths and the auth behaviour are known; the response bodies are not.
/// Re-capture these against a real key before trusting the field names.</para>
/// </summary>
public class PwsClientTests
{
    private const double LatDeg = 35.2226;
    private const double LonDeg = -97.4395;

    /// <summary>
    /// The location service answers in parallel arrays — one per field, indexed together —
    /// rather than an array of objects, which is the shape most likely to be got wrong.
    /// </summary>
    [Fact]
    public void NearbyReadsParallelArraysAndSortsByDistance()
    {
        using var json = JsonDocument.Parse("""
        {"location": {
          "stationId":   ["KOKFAR",  "KOKNEAR", "KOKMID"],
          "stationName": ["Far one", "Near one", "Middle"],
          "latitude":    [35.4026,   35.2236,   35.3026],
          "longitude":   [-97.4395,  -97.4400,  -97.4395],
          "qcStatus":    [1,         1,         1]
        }}
        """);

        var stations = PwsClient.ParseNearby(json, LatDeg, LonDeg);

        Assert.Equal(3, stations.Count);
        Assert.Equal(["KOKNEAR", "KOKMID", "KOKFAR"], stations.Select(s => s.Id));
        Assert.Equal("Near one", stations[0].Name);
        Assert.All(stations, s => Assert.Equal(StationSource.Personal, s.Source));

        // Our own geodesy, as with the NWS list: about 0.001° of latitude.
        Assert.InRange(stations[0].DistanceKm, 0.09, 0.15);
        for (int i = 1; i < stations.Count; i++)
            Assert.True(stations[i].DistanceKm >= stations[i - 1].DistanceKm);
    }

    /// <summary>
    /// 1 passed, 0 not checked, -1 failed. "Not checked" is null rather than true — the
    /// property claims the station passed, and an unchecked one has not — but only an
    /// explicit failure keeps a station out of the automatic pick.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, null)]
    [InlineData(-1, false)]
    public void QualityStatusDistinguishesUncheckedFromFailed(int qcStatus, bool? expected)
    {
        using var json = JsonDocument.Parse($$"""
        {"location": {
          "stationId": ["KOKONE"], "stationName": ["One"],
          "latitude": [35.2226], "longitude": [-97.4395], "qcStatus": [{{qcStatus}}]
        } }
        """);

        Assert.Equal(expected, PwsClient.ParseNearby(json, LatDeg, LonDeg)[0].PassedQualityCheck);
    }

    /// <summary>
    /// A short or absent array is not an error — every field is read by index against the
    /// shortest one that matters, so a list missing its names still yields usable stations.
    /// </summary>
    [Fact]
    public void RaggedArraysYieldWhatTheyCan()
    {
        using var json = JsonDocument.Parse("""
        {"location": {
          "stationId": ["KOKONE", "KOKTWO", "KOKTHREE"],
          "stationName": ["Only one name"],
          "latitude": [35.2226, 35.2326],
          "longitude": [-97.4395, -97.4495]
        }}
        """);

        var stations = PwsClient.ParseNearby(json, LatDeg, LonDeg);

        // Two have coordinates; the third has an id and nowhere to put it.
        Assert.Equal(2, stations.Count);
        Assert.Equal("Only one name", stations[0].Name);
        Assert.Equal("KOKTWO", stations[1].Name);   // falls back to the id
        Assert.All(stations, s => Assert.Null(s.PassedQualityCheck));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"location": null}""")]
    [InlineData("""{"location": {}}""")]
    public void AnEmptyNearbyResponseIsNotAFailure(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Empty(PwsClient.ParseNearby(json, LatDeg, LonDeg));
    }

    /// <summary>
    /// The readings live in a nested <c>metric</c> object; the unit-free ones — wind
    /// direction, humidity — sit at the top level, so both have to be read.
    /// </summary>
    [Fact]
    public void ObservationReadsBothLevelsAndConvertsPressure()
    {
        var station = new ObservationStation("KOKONE", "KOKONE", 35.2226, -97.4395, 1.2, 90)
        {
            Source = StationSource.Personal,
        };

        using var json = JsonDocument.Parse("""
        {"observations": [{
          "stationID": "KOKONE",
          "obsTimeUtc": "2026-09-07T03:00:00Z",
          "neighborhood": "Cedar Ridge",
          "humidity": 46, "winddir": 190,
          "metric": {"temp": 24, "dewpt": 12, "windSpeed": 9, "windGust": 14,
                     "heatIndex": 27, "windChill": 24, "pressure": 1015.24,
                     "precipRate": 0.0, "elev": 1400}
        }]}
        """);

        var current = PwsClient.ParseObservation(json, station);

        Assert.NotNull(current);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 3, 0, 0, TimeSpan.Zero), current!.ObservedUtc);
        Assert.Equal(24, current.TemperatureC);
        Assert.Equal(12, current.DewpointC);
        Assert.Equal(46, current.RelativeHumidityPercent);
        Assert.Equal(190, current.WindDirectionDeg);
        Assert.Equal(9, current.WindSpeedKmh);
        Assert.Equal(14, current.WindGustKmh);
        Assert.Equal(27, current.FeelsLikeC);

        // Reported in hectopascals; stored in pascals, as api.weather.gov sends them.
        Assert.Equal(101524.0, current.PressurePa!.Value, 1);

        // No domestic station has a visibility sensor. Absent, not zero.
        Assert.Null(current.VisibilityM);

        // The neighbourhood is what the owner called the place, and beats the callsign.
        Assert.Equal("Cedar Ridge", current.Station.Name);
        Assert.Equal(StationSource.Personal, current.Station.Source);
    }

    /// <summary>
    /// Weather Underground restates the air temperature as heat index and wind chill whenever
    /// neither applies, rather than omitting them — so taken at face value every mild day
    /// claims a "feels like" that says nothing.
    /// </summary>
    [Fact]
    public void FeelsLikeIsDroppedWhenItMerelyRestatesTheAirTemperature()
    {
        using var json = JsonDocument.Parse("""
        {"observations": [{
          "obsTimeUtc": "2026-09-07T03:00:00Z",
          "metric": {"temp": 24, "heatIndex": 24, "windChill": 24}
        }]}
        """);

        var current = PwsClient.ParseObservation(json, Station());
        Assert.NotNull(current);
        Assert.Null(current!.FeelsLikeC);
    }

    /// <summary>A station reports numbers; it has no observer and no ceilometer.</summary>
    [Fact]
    public void ObservationCarriesNoSkyDescription()
    {
        using var json = JsonDocument.Parse("""
        {"observations": [{"obsTimeUtc": "2026-09-07T03:00:00Z", "metric": {"temp": 24}}]}
        """);

        var current = PwsClient.ParseObservation(json, Station());
        Assert.NotNull(current);
        Assert.Equal("", current!.Description);
        Assert.Equal("", current.Sky);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"observations": []}""")]
    [InlineData("""{"observations": null}""")]
    [InlineData("""{"observations": [{"metric": {"temp": 24}}]}""")]        // no timestamp
    [InlineData("""{"observations": [{"obsTimeUtc": null}]}""")]
    public void AnEmptyObservationIsSkippedRatherThanThrown(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Null(PwsClient.ParseObservation(json, Station()));
    }

    /// <summary>A station with no <c>metric</c> block at all must not throw.</summary>
    [Fact]
    public void AMissingMetricBlockLeavesEveryReadingNull()
    {
        using var json = JsonDocument.Parse("""
        {"observations": [{"obsTimeUtc": "2026-09-07T03:00:00Z", "humidity": 46}]}
        """);

        var current = PwsClient.ParseObservation(json, Station());

        Assert.NotNull(current);
        Assert.Null(current!.TemperatureC);
        Assert.Null(current.PressurePa);
        Assert.Equal(46, current.RelativeHumidityPercent);
    }

    private static ObservationStation Station() =>
        new("KOKONE", "KOKONE", LatDeg, LonDeg, 1.0, 90) { Source = StationSource.Personal };
}
