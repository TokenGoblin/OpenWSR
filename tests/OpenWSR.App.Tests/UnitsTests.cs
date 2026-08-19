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
}
