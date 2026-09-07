using OpenWSR.App;

namespace OpenWSR.App.Tests;

/// <summary>
/// <see cref="Units"/> drives every user-facing measurement in the app, and it is static
/// mutable state, so a mis-set system silently changes every readout at once.
/// </summary>
[Collection("units")]
public class UnitsTests : IDisposable
{
    private readonly UnitSystem _original = Units.System;

    public void Dispose() => Units.System = _original;

    [Theory]
    [InlineData(UnitSystem.Metric, "100.0 km")]
    [InlineData(UnitSystem.Imperial, "62.1 mi")]
    [InlineData(UnitSystem.Nautical, "54.0 nm")]
    public void DistanceConvertsAndLabels(UnitSystem system, string expected)
    {
        Units.System = system;
        Assert.Equal(expected, Units.Distance(100));
    }

    [Theory]
    [InlineData(UnitSystem.Metric, "100.00 km")]
    [InlineData(UnitSystem.Imperial, "62.14 mi")]
    [InlineData(UnitSystem.Nautical, "54.00 nm")]
    public void PreciseDistanceKeepsTwoDecimals(UnitSystem system, string expected)
    {
        Units.System = system;
        Assert.Equal(expected, Units.DistancePrecise(100));
    }

    [Theory]
    [InlineData(UnitSystem.Metric, "80 km/h")]
    [InlineData(UnitSystem.Imperial, "50 mph")]
    [InlineData(UnitSystem.Nautical, "43 kt")]
    public void SpeedConvertsAndLabels(UnitSystem system, string expected)
    {
        Units.System = system;
        Assert.Equal(expected, Units.Speed(80));
    }

    [Theory]
    [InlineData(UnitSystem.Metric, "1000 m")]
    [InlineData(UnitSystem.Imperial, "3281 ft")]
    [InlineData(UnitSystem.Nautical, "3281 ft")] // aviation convention: feet, not metres
    public void HeightUsesFeetOutsideMetric(UnitSystem system, string expected)
    {
        Units.System = system;
        Assert.Equal(expected, Units.Height(1000));
    }

    [Theory]
    [InlineData(UnitSystem.Metric, "3048 m")]
    [InlineData(UnitSystem.Imperial, "10 kft")]
    public void EchoTopHeightsRoundTrip(UnitSystem system, string expected)
    {
        Units.System = system;
        Assert.Equal(expected, Units.HeightKft(10));
    }

    [Fact]
    public void ZeroIsFormattedNotBlank()
    {
        Units.System = UnitSystem.Imperial;
        Assert.Equal("0.0 mi", Units.Distance(0));
        Assert.Equal("0 mph", Units.Speed(0));
    }

    [Theory]
    [InlineData(UnitSystem.Metric, "20 °C")]
    [InlineData(UnitSystem.Imperial, "68 °F")]
    // Nautical follows Imperial: choosing knots for radar ranges is not a request to read
    // the outside temperature in Celsius.
    [InlineData(UnitSystem.Nautical, "68 °F")]
    public void TemperatureConvertsOutsideMetric(UnitSystem system, string expected)
    {
        Units.System = system;
        Assert.Equal(expected, Units.Temperature(20));
    }

    /// <summary>Below freezing is where a dropped minus sign would matter most.</summary>
    [Theory]
    [InlineData(UnitSystem.Metric, "-10 °C")]
    [InlineData(UnitSystem.Imperial, "14 °F")]
    public void TemperatureKeepsItsSign(UnitSystem system, string expected)
    {
        Units.System = system;
        Assert.Equal(expected, Units.Temperature(-10));
    }

    /// <summary>
    /// .NET's formatting is IEEE-correct, so a fraction below freezing rounds to negative zero
    /// and "F0" prints it as "-0" — which reads as a bug rather than as a temperature. Both
    /// scales cross zero in ordinary weather, so both are checked. Rounding itself is left
    /// alone: "F0" rounds half to even, so -0.5 lands on zero like 0.5 does, and only a value
    /// past the half degree keeps its sign.
    /// </summary>
    [Theory]
    [InlineData(UnitSystem.Metric, -0.4, "0 °C")]
    [InlineData(UnitSystem.Metric, -0.5, "0 °C")]
    [InlineData(UnitSystem.Metric, -0.6, "-1 °C")]     // past the half degree, and stays signed
    [InlineData(UnitSystem.Metric, 0.4, "0 °C")]
    [InlineData(UnitSystem.Imperial, -17.9, "0 °F")]   // -17.9 °C is -0.22 °F
    [InlineData(UnitSystem.Imperial, -18.0, "0 °F")]   // exactly -0.4 °F
    [InlineData(UnitSystem.Imperial, -18.2, "-1 °F")]
    public void TemperatureNeverPrintsNegativeZero(UnitSystem system, double celsius, string expected)
    {
        Units.System = system;
        Assert.Equal(expected, Units.Temperature(celsius));
    }

    /// <summary>
    /// The map label drops the unit letter — the glyph atlas has wide fixed tracking, so
    /// "73 °F" sprawls to nearly twice the width of "73°" and collides with the place names
    /// baked into the basemap. Which scale is meant is said once by the caption instead.
    /// </summary>
    [Theory]
    [InlineData(UnitSystem.Metric, "20°", "°C")]
    [InlineData(UnitSystem.Imperial, "68°", "°F")]
    [InlineData(UnitSystem.Nautical, "68°", "°F")]
    public void ShortTemperatureCarriesNoUnitLetter(
        UnitSystem system, string expected, string expectedUnit)
    {
        Units.System = system;
        Assert.Equal(expected, Units.TemperatureShort(20));
        Assert.Equal(expectedUnit, Units.TemperatureUnit);
    }

    /// <summary>
    /// Rainfall wants different resolutions in the two systems: 0.01 in is the tipping-bucket
    /// increment most gauges report, and 0.3 mm is the same quantity, so one decimal in
    /// millimetres and two in inches show the same real precision.
    /// </summary>
    [Theory]
    // 6.35 mm is exactly a quarter inch. It renders as 6.3 rather than 6.4 because 6.35 is not
    // exactly representable in binary and lands a hair below the midpoint — correct rounding of
    // the number actually held, and not worth forcing.
    [InlineData(UnitSystem.Metric, 6.35, "6.3 mm")]
    [InlineData(UnitSystem.Imperial, 6.35, "0.25 in")]
    [InlineData(UnitSystem.Metric, 12.7, "12.7 mm")]
    [InlineData(UnitSystem.Metric, 0.0, "0.0 mm")]
    [InlineData(UnitSystem.Imperial, 0.254, "0.01 in")]
    public void RainfallUsesInchesOutsideMetric(UnitSystem system, double mm, string expected)
    {
        Units.System = system;
        Assert.Equal(expected, Units.Rainfall(mm));
    }

    [Theory]
    [InlineData(UnitSystem.Metric, "1015 hPa")]
    [InlineData(UnitSystem.Imperial, "29.98 inHg")]
    public void PressureIsInchesOfMercuryOutsideMetric(UnitSystem system, string expected)
    {
        Units.System = system;
        // A real KBOS reading, in the pascals api.weather.gov reports.
        Assert.Equal(expected, Units.Pressure(101523.93));
    }
}
