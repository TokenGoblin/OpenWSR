using OpenWSR.Geo;

namespace OpenWSR.Geo.Tests;

/// <summary>
/// Solar geometry, checked against <b>pvlib 0.15.2</b> running NREL's SPA — an independent
/// implementation, per this project's standard, and a far more elaborate one than what is
/// under test here.
///
/// The tolerance is a tenth of a degree. That is the honest accuracy of the low-precision
/// Almanac equations against SPA, and it is three orders of magnitude finer than anything
/// this is used for: the terminator blend spans ten degrees of zenith angle.
/// </summary>
public class SolarPositionTests
{
    private const double ToleranceDeg = 0.1;

    [Theory]
    [InlineData("2026-08-23T18:00:00", 40.3, -111.9, 35.213837)]     // Utah, midday
    [InlineData("2026-08-23T04:00:00", 40.3, -111.9, 109.016365)]    // Utah, night
    [InlineData("2026-06-21T12:00:00", 51.48, 0.0, 28.045430)]       // Greenwich, solstice
    [InlineData("2026-12-21T12:00:00", 51.48, 0.0, 74.920526)]       // Greenwich, winter
    [InlineData("2026-03-20T06:00:00", 25.0, -80.0, 153.933623)]     // Florida, deep night
    [InlineData("2026-01-15T22:00:00", -33.87, 151.21, 55.014695)]   // Sydney, southern summer
    [InlineData("2026-08-23T12:00:00", 0.0, 0.0, 11.353967)]         // equator, prime meridian
    [InlineData("2026-11-05T23:30:00", 61.2, -149.9, 79.999927)]     // Anchorage, low sun
    public void ZenithMatchesPvlib(string utc, double latDeg, double lonDeg, double expectedDeg)
    {
        var time = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var moment = DateTime.SpecifyKind(time, DateTimeKind.Utc);

        Assert.Equal(expectedDeg, SolarPosition.ZenithDeg(latDeg, lonDeg, moment), ToleranceDeg);
    }

    /// <summary>
    /// Physics rather than a reference value: at the equinox the sun stands directly over the
    /// equator at the meridian where it is solar noon, so the zenith angle there is nearly
    /// zero. The residual is the equation of time, which is what makes it "nearly".
    /// </summary>
    [Fact]
    public void TheSunIsOverheadAtTheSubsolarPoint()
    {
        var equinox = new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc);
        double zenith = SolarPosition.ZenithDeg(0, 0, equinox);

        Assert.InRange(zenith, 0, 3);
    }

    /// <summary>
    /// Midsummer inside the Arctic circle: the sun does not set, so it stays above the
    /// horizon around the whole clock.
    /// </summary>
    [Fact]
    public void TheMidnightSunNeverSets()
    {
        for (int hour = 0; hour < 24; hour++)
        {
            var moment = new DateTime(2026, 6, 21, hour, 0, 0, DateTimeKind.Utc);
            double zenith = SolarPosition.ZenithDeg(78.2, 15.6, moment);   // Svalbard
            Assert.True(zenith < 90, $"the sun was below the horizon at {hour:D2}:00 UTC");
        }
    }

    /// <summary>And the same latitude in midwinter never sees it.</summary>
    [Fact]
    public void ThePolarNightNeverDawns()
    {
        for (int hour = 0; hour < 24; hour++)
        {
            var moment = new DateTime(2026, 12, 21, hour, 0, 0, DateTimeKind.Utc);
            Assert.True(SolarPosition.ZenithDeg(78.2, 15.6, moment) > 90,
                $"the sun rose at {hour:D2}:00 UTC inside the polar night");
        }
    }

    /// <summary>
    /// Noon is the lowest zenith angle of the day, wherever you are. This catches an equation
    /// of time applied with the wrong sign, which shifts the whole day by a quarter of an
    /// hour and is otherwise invisible in a spot check.
    /// </summary>
    [Fact]
    public void TheSunIsHighestNearLocalNoon()
    {
        // 111.9°W is 7.46 hours behind Greenwich, so local noon is about 19:28 UTC.
        double best = double.MaxValue;
        int bestMinute = -1;
        for (int minute = 0; minute < 1440; minute += 5)
        {
            double zenith = SolarPosition.ZenithDeg(
                40.3, -111.9, new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute));
            if (zenith < best) { best = zenith; bestMinute = minute; }
        }

        Assert.InRange(bestMinute, 19 * 60, 20 * 60);
    }

    [Theory]
    [InlineData(0, 1.0)]        // sun overhead
    [InlineData(60, 1.0)]       // full day
    [InlineData(85, 1.0)]       // the last of full day
    [InlineData(95, 0.0)]       // properly dark
    [InlineData(140, 0.0)]      // the middle of the night
    public void DaylightIsFullOrAbsentOutsideTheTwilightBand(double zenithDeg, double expected) =>
        Assert.Equal(expected, SolarPosition.DaylightFraction(zenithDeg), 6);

    /// <summary>
    /// The blend has to be continuous and monotone. A step at 90° draws a hard line across
    /// the map that visibly sweeps westward through the day; a non-monotone blend puts a
    /// bright band inside the twilight zone.
    /// </summary>
    [Fact]
    public void TwilightFadesSmoothlyAndOnlyDownwards()
    {
        double previous = 1.0;
        for (double zenith = 84; zenith <= 96; zenith += 0.25)
        {
            double fraction = SolarPosition.DaylightFraction(zenith);
            Assert.InRange(fraction, 0.0, 1.0);
            Assert.True(fraction <= previous + 1e-12,
                $"daylight increased going into the night at {zenith}°");
            Assert.True(previous - fraction < 0.2,
                $"the blend jumped by {previous - fraction:F3} at {zenith}° — that is an edge");
            previous = fraction;
        }
        Assert.Equal(0.0, previous, 6);
    }

    [Fact]
    public void TheMidpointOfTwilightIsHalfLit() =>
        Assert.Equal(0.5, SolarPosition.DaylightFraction(90), 3);

    /// <summary>J2000.0 is 2451545.0 by definition — the epoch the whole series is built on.</summary>
    [Fact]
    public void JulianDayIsAnchoredAtJ2000() =>
        Assert.Equal(2451545.0,
            SolarPosition.JulianDay(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)), 6);
}
