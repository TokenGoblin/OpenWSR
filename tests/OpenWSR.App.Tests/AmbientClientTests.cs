using System.Text.Json;
using OpenWSR.Ingest;

namespace OpenWSR.App.Tests;

/// <summary>
/// The user's own station, through the Ambient Weather Network.
///
/// <para><b>Built from Ambient's published response shapes, not captured live</b> — the API
/// needs an account that owns a station. The endpoint and its auth behaviour *were* verified:
/// <c>rt.ambientweather.net/v1/devices</c> answers 401 <c>{"error":"apiKey-missing"}</c> with no
/// keys and <c>{"error":"applicationKey-invalid"}</c> with junk ones. The field names come from
/// Ambient's Device Data Specs. Re-capture against a real account before trusting them.</para>
///
/// <para>The conversions are the part worth testing hardest. Ambient publishes imperial with no
/// way to ask for anything else — °F, mph, inHg, inches — so every reading crosses a unit
/// boundary on the way in, and a wrong factor here is a plausible-looking number rather than a
/// crash.</para>
/// </summary>
public class AmbientClientTests
{
    private const double LatDeg = 35.2226;
    private const double LonDeg = -97.4395;

    /// <summary>
    /// Ambient has no per-device current endpoint, so reading one station means re-reading the
    /// account and picking it out. The helper exists so a caller wanting one station cannot be
    /// tempted to route a MAC address at a service that has never heard of it — which is
    /// exactly what the station picker did until it was caught.
    /// </summary>
    [Fact]
    public void OneDeviceIsFoundAmongSeveralByItsMacAddress()
    {
        using var json = JsonDocument.Parse("""
        [
          {"macAddress": "AA:BB:CC:DD:EE:01", "info": {"name": "Backyard"},
           "lastData": {"dateutc": 1757212800000, "tempf": 68.0}},
          {"macAddress": "AA:BB:CC:DD:EE:02", "info": {"name": "Cabin"},
           "lastData": {"dateutc": 1757212800000, "tempf": 50.0}}
        ]
        """);

        var devices = AmbientClient.ParseDevices(json, LatDeg, LonDeg);
        var cabin = devices.Single(d => d.Station.Id == "AA:BB:CC:DD:EE:02");

        Assert.Equal("Cabin", cabin.Station.Name);
        Assert.Equal(10.0, cabin.TemperatureC!.Value, 6);   // 50 °F
        Assert.DoesNotContain(devices, d => d.Station.Id == "AA:BB:CC:DD:EE:03");
    }

    private static JsonDocument Device(string lastData, string info = """{"name": "Backyard"}""") =>
        JsonDocument.Parse($$"""
        [ { "macAddress": "AA:BB:CC:DD:EE:FF", "info": {{info}}, "lastData": {{lastData}} } ]
        """);

    [Fact]
    public void ImperialReadingsAreConvertedOnTheWayIn()
    {
        using var json = Device("""
        {
          "dateutc": 1757212800000,
          "tempf": 68.0, "dewPoint": 50.0, "feelsLike": 68.0,
          "humidity": 46, "winddir": 190,
          "windspeedmph": 10.0, "windgustmph": 25.0,
          "baromrelin": 29.92, "baromabsin": 25.10,
          "hourlyrainin": 0.25, "solarradiation": 350.5, "uv": 4
        }
        """);

        var devices = AmbientClient.ParseDevices(json, LatDeg, LonDeg);
        Assert.Single(devices);
        var d = devices[0];

        Assert.Equal(20.0, d.TemperatureC!.Value, 6);           // 68 °F
        Assert.Equal(10.0, d.DewpointC!.Value, 6);              // 50 °F
        Assert.Equal(46, d.RelativeHumidityPercent);
        Assert.Equal(190, d.WindDirectionDeg);
        Assert.Equal(16.09344, d.WindSpeedKmh!.Value, 5);       // 10 mph
        Assert.Equal(40.2336, d.WindGustKmh!.Value, 4);         // 25 mph
        Assert.Equal(6.35, d.PrecipitationLastHourMm!.Value, 6);// 0.25 in

        // 29.92 inHg is one standard atmosphere to within a few pascals.
        Assert.InRange(d.PressurePa!.Value, 101300, 101360);

        // Milliseconds since the epoch, not seconds — out by a factor of 1000 the timestamp
        // lands in 1970 and every reading looks impossibly stale.
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1757212800000), d.ObservedUtc);
    }

    /// <summary>
    /// Relative pressure is the sea-level-corrected figure a weather report means; absolute is
    /// the raw sensor reading. Preferring the wrong one is a plausible number off by however
    /// high the station sits — hundreds of hectopascals at altitude.
    /// </summary>
    [Fact]
    public void RelativePressureWinsAndAbsoluteIsOnlyAFallback()
    {
        using var both = Device("""{"dateutc": 1757212800000, "baromrelin": 29.92, "baromabsin": 25.10}""");
        var withBoth = AmbientClient.ParseDevices(both, LatDeg, LonDeg)[0];
        Assert.InRange(withBoth.PressurePa!.Value, 101300, 101360);

        using var absOnly = Device("""{"dateutc": 1757212800000, "baromabsin": 25.10}""");
        var fallback = AmbientClient.ParseDevices(absOnly, LatDeg, LonDeg)[0];
        Assert.InRange(fallback.PressurePa!.Value, 84900, 85100);
    }

    /// <summary>
    /// Ambient computes feelsLike as wind chill below 50 °F and heat index above 68 °F, and
    /// repeats the air temperature in between — so a naive read gives every mild day a "feels
    /// like" that says nothing.
    /// </summary>
    [Fact]
    public void FeelsLikeIsDroppedWhenItMerelyRestatesTheAirTemperature()
    {
        using var same = Device("""{"dateutc": 1757212800000, "tempf": 68.0, "feelsLike": 68.0}""");
        Assert.Null(AmbientClient.ParseDevices(same, LatDeg, LonDeg)[0].FeelsLikeC);

        using var hot = Device("""{"dateutc": 1757212800000, "tempf": 95.0, "feelsLike": 105.0}""");
        var real = AmbientClient.ParseDevices(hot, LatDeg, LonDeg)[0];
        Assert.Equal(40.5555555, real.FeelsLikeC!.Value, 5);    // 105 °F
    }

    /// <summary>
    /// The published REST example omits coordinates entirely while real accounts include them,
    /// so both shapes have to work. A device that does not say where it is, is at the place
    /// being forecast for — the only assumption available, and the right one for a device you
    /// own and installed.
    /// </summary>
    [Fact]
    public void CoordinatesAreReadWhenPresent()
    {
        using var json = Device(
            """{"dateutc": 1757212800000, "tempf": 68.0}""",
            info: """{"name": "Backyard", "coords": {"coords": {"lat": 35.2326, "lon": -97.4395}}}""");

        var station = AmbientClient.ParseDevices(json, LatDeg, LonDeg)[0].Station;

        Assert.Equal(35.2326, station.LatDeg, 4);
        Assert.InRange(station.DistanceKm, 1.0, 1.2);     // about 0.01° of latitude
    }

    [Fact]
    public void AStationWithNoCoordinatesSitsAtThePlaceItself()
    {
        using var json = Device("""{"dateutc": 1757212800000, "tempf": 68.0}""");
        var station = AmbientClient.ParseDevices(json, LatDeg, LonDeg)[0].Station;

        Assert.Equal(LatDeg, station.LatDeg);
        Assert.Equal(LonDeg, station.LonDeg);
        Assert.Equal(0.0, station.DistanceKm, 6);
    }

    [Fact]
    public void TheDeviceIsMarkedAsTheUsersOwn()
    {
        using var json = Device("""{"dateutc": 1757212800000, "tempf": 68.0}""");
        var d = AmbientClient.ParseDevices(json, LatDeg, LonDeg)[0];

        Assert.Equal(StationSource.Own, d.Station.Source);
        Assert.Equal("AA:BB:CC:DD:EE:FF", d.Station.Id);
        Assert.Equal("Backyard", d.Station.Name);

        // No observer and no ceilometer, the same as any other domestic station.
        Assert.Equal("", d.Description);
        Assert.Null(d.VisibilityM);
    }

    /// <summary>The name falls back through <c>location</c> to something rather than blank.</summary>
    [Theory]
    [InlineData("""{"name": "Backyard", "location": "Home"}""", "Backyard")]
    [InlineData("""{"location": "Home"}""", "Home")]
    [InlineData("""{}""", "My weather station")]
    [InlineData("""null""", "My weather station")]
    public void TheDeviceIsAlwaysNamed(string info, string expected)
    {
        using var json = Device("""{"dateutc": 1757212800000, "tempf": 68.0}""", info);
        Assert.Equal(expected, AmbientClient.ParseDevices(json, LatDeg, LonDeg)[0].Station.Name);
    }

    /// <summary>
    /// A device that has never reported has no <c>dateutc</c>, and an observation with no time
    /// cannot be aged — so it is not an observation and must not be offered as one.
    /// </summary>
    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"tempf": 68.0}""")]
    [InlineData("""null""")]
    public void ADeviceWithNoTimestampIsNotAReading(string lastData)
    {
        using var json = Device(lastData);
        Assert.Empty(AmbientClient.ParseDevices(json, LatDeg, LonDeg));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{}""")]
    [InlineData("""[{"info": {"name": "No mac"}, "lastData": {"dateutc": 1757212800000}}]""")]
    public void AnEmptyOrMalformedResponseYieldsNothing(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Empty(AmbientClient.ParseDevices(json, LatDeg, LonDeg));
    }

    /// <summary>An account can own more than one station; both are offered.</summary>
    [Fact]
    public void EveryDeviceOnTheAccountIsReturned()
    {
        using var json = JsonDocument.Parse("""
        [
          {"macAddress": "AA:BB:CC:DD:EE:01", "info": {"name": "Backyard"},
           "lastData": {"dateutc": 1757212800000, "tempf": 68.0}},
          {"macAddress": "AA:BB:CC:DD:EE:02", "info": {"name": "Cabin"},
           "lastData": {"dateutc": 1757212800000, "tempf": 50.0}}
        ]
        """);

        var devices = AmbientClient.ParseDevices(json, LatDeg, LonDeg);

        Assert.Equal(2, devices.Count);
        Assert.Equal(["Backyard", "Cabin"], devices.Select(d => d.Station.Name));
        Assert.All(devices, d => Assert.Equal(StationSource.Own, d.Station.Source));
    }
}
