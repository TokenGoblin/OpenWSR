namespace OpenWSR.Geo;

/// <summary>
/// Where the sun is, which is what decides whether a satellite pixel should be read as
/// reflected sunlight or as emitted heat.
///
/// The quantity that matters here is the <em>solar zenith angle</em> — the angle between
/// straight up and the sun. Below about 85° there is enough light for the visible bands to
/// mean anything; past about 95° the ground is properly dark and only the infrared is
/// telling you anything. Between the two is twilight, and the terminator has to be crossed
/// as a blend rather than a step or it draws a hard line across the map that moves.
///
/// Implemented from the NOAA Solar Calculator's equations, which are the low-precision
/// Astronomical Almanac ones: good to well under a minute of arc for any date this century.
/// That is far finer than a blend spanning ten degrees needs, and it avoids depending on an
/// ephemeris for what is ultimately a shading decision.
/// </summary>
public static class SolarPosition
{
    /// <summary>
    /// Solar zenith angle in degrees: 0 with the sun overhead, 90 at the horizon, 180 at the
    /// antipode. Geometric — no refraction correction, which matters at sunrise and sunset
    /// by about half a degree and does not matter to a blend at all.
    /// </summary>
    public static double ZenithDeg(double latDeg, double lonDeg, DateTime utc)
    {
        double julianCentury = (JulianDay(utc) - 2451545.0) / 36525.0;

        double geomMeanLong = Wrap360(280.46646 + julianCentury
            * (36000.76983 + julianCentury * 0.0003032));
        double geomMeanAnom = 357.52911 + julianCentury
            * (35999.05029 - 0.0001537 * julianCentury);
        double eccentricity = 0.016708634 - julianCentury
            * (0.000042037 + 0.0000001267 * julianCentury);

        double anomRad = Radians(geomMeanAnom);
        double centre = Math.Sin(anomRad) * (1.914602 - julianCentury * (0.004817 + 0.000014 * julianCentury))
                      + Math.Sin(2 * anomRad) * (0.019993 - 0.000101 * julianCentury)
                      + Math.Sin(3 * anomRad) * 0.000289;

        double trueLong = geomMeanLong + centre;
        double apparentLong = trueLong - 0.00569
            - 0.00478 * Math.Sin(Radians(125.04 - 1934.136 * julianCentury));

        double meanObliquity = 23.0 + (26.0 + (21.448 - julianCentury
            * (46.815 + julianCentury * (0.00059 - julianCentury * 0.001813))) / 60.0) / 60.0;
        double obliquity = meanObliquity
            + 0.00256 * Math.Cos(Radians(125.04 - 1934.136 * julianCentury));

        double declination = Degrees(Math.Asin(
            Math.Sin(Radians(obliquity)) * Math.Sin(Radians(apparentLong))));

        // The equation of time: the difference between clock noon and the sun actually
        // crossing the meridian, which runs to a quarter of an hour either way over a year.
        double varY = Math.Tan(Radians(obliquity / 2)) * Math.Tan(Radians(obliquity / 2));
        double meanLongRad = Radians(geomMeanLong);
        double equationOfTime = 4.0 * Degrees(
            varY * Math.Sin(2 * meanLongRad)
            - 2 * eccentricity * Math.Sin(anomRad)
            + 4 * eccentricity * varY * Math.Sin(anomRad) * Math.Cos(2 * meanLongRad)
            - 0.5 * varY * varY * Math.Sin(4 * meanLongRad)
            - 1.25 * eccentricity * eccentricity * Math.Sin(2 * anomRad));

        double minutesUtc = utc.TimeOfDay.TotalMinutes;
        double trueSolarTime = Mod(minutesUtc + equationOfTime + 4.0 * lonDeg, 1440.0);
        double hourAngle = trueSolarTime / 4.0 < 0
            ? trueSolarTime / 4.0 + 180.0
            : trueSolarTime / 4.0 - 180.0;

        double latRad = Radians(latDeg);
        double decRad = Radians(declination);
        double cosZenith = Math.Sin(latRad) * Math.Sin(decRad)
            + Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(Radians(hourAngle));

        return Degrees(Math.Acos(Math.Clamp(cosZenith, -1.0, 1.0)));
    }

    /// <summary>
    /// How daylit a point is, 0 (night) to 1 (full day), across the twilight band.
    ///
    /// The edges are deliberately wide. A step at exactly 90° would draw a hard line across
    /// the map that visibly sweeps westward, and the visible bands do not fail at the
    /// horizon — they fade, growing noisy and reddened through twilight. Blending over the
    /// last few degrees is both the honest reading of the data and the one that does not
    /// call attention to itself.
    /// </summary>
    public static double DaylightFraction(double zenithDeg, double fullDayDeg = 85, double fullNightDeg = 95)
    {
        if (zenithDeg <= fullDayDeg) return 1.0;
        if (zenithDeg >= fullNightDeg) return 0.0;
        double t = (zenithDeg - fullDayDeg) / (fullNightDeg - fullDayDeg);
        // Smoothstep rather than linear: no visible edge where the blend starts and ends.
        return 1.0 - t * t * (3.0 - 2.0 * t);
    }

    /// <summary>
    /// Julian day for a UTC instant, by the Fliegel–Van Flandern convention the Almanac uses.
    /// </summary>
    internal static double JulianDay(DateTime utc)
    {
        int year = utc.Year, month = utc.Month;
        double day = utc.Day + utc.TimeOfDay.TotalDays;
        if (month <= 2) { year -= 1; month += 12; }

        int a = year / 100;
        int b = 2 - a + a / 4;   // Gregorian correction
        return Math.Floor(365.25 * (year + 4716))
             + Math.Floor(30.6001 * (month + 1))
             + day + b - 1524.5;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180.0;
    private static double Degrees(double radians) => radians * 180.0 / Math.PI;
    private static double Wrap360(double degrees) => Mod(degrees, 360.0);

    /// <summary>Always non-negative, unlike <c>%</c> on a negative left operand.</summary>
    private static double Mod(double value, double modulus)
    {
        double r = value % modulus;
        return r < 0 ? r + modulus : r;
    }
}
