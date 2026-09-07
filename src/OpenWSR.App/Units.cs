namespace OpenWSR.App;

public enum UnitSystem
{
    /// <summary>Kilometres, km/h, metres.</summary>
    Metric,
    /// <summary>Miles, mph, feet.</summary>
    Imperial,
    /// <summary>Nautical miles, knots, feet — the aviation/severe-weather convention.</summary>
    Nautical,
}

/// <summary>Display formatting for distances, speeds and heights in the user's chosen units.</summary>
public static class Units
{
    public static UnitSystem System { get; set; } = UnitSystem.Imperial;

    public static string Distance(double kilometres) => System switch
    {
        UnitSystem.Imperial => $"{kilometres * 0.621371:F1} mi",
        UnitSystem.Nautical => $"{kilometres * 0.539957:F1} nm",
        _ => $"{kilometres:F1} km",
    };

    /// <summary>Distance with more precision, for the measuring tool.</summary>
    public static string DistancePrecise(double kilometres) => System switch
    {
        UnitSystem.Imperial => $"{kilometres * 0.621371:F2} mi",
        UnitSystem.Nautical => $"{kilometres * 0.539957:F2} nm",
        _ => $"{kilometres:F2} km",
    };

    public static string Speed(double kilometresPerHour) => System switch
    {
        UnitSystem.Imperial => $"{kilometresPerHour * 0.621371:F0} mph",
        UnitSystem.Nautical => $"{kilometresPerHour * 0.539957:F0} kt",
        _ => $"{kilometresPerHour:F0} km/h",
    };

    /// <summary>
    /// A short distance in metres or feet — a GPS accuracy radius, a beam height. Miles are
    /// the wrong unit at this scale: a 141 m fix formatted by <see cref="Distance"/> reads
    /// "0.1 mi", which throws away the difference between a good fix and a useless one.
    /// </summary>
    public static string ShortDistance(double metres) => System switch
    {
        UnitSystem.Metric => $"{metres:F0} m",
        _ => $"{metres * 3.28084:F0} ft",
    };

    /// <summary>Beam or echo height. Metric stays in metres; the others use feet.</summary>
    public static string Height(double metres) => ShortDistance(metres);

    public static string HeightKft(double kilofeet) => System switch
    {
        UnitSystem.Metric => $"{kilofeet * 304.8:F0} m",
        _ => $"{kilofeet:F0} kft",
    };

    /// <summary>
    /// Air temperature. Nautical follows Imperial rather than Metric: the unit system here
    /// picks distances and speeds for reading a radar, and nobody who chose knots thereby
    /// asked to read the outside temperature in Celsius.
    /// </summary>
    public static string Temperature(double celsius) => System switch
    {
        UnitSystem.Metric => $"{Whole(celsius)} °C",
        _ => $"{Whole(celsius * 9.0 / 5.0 + 32.0)} °F",
    };

    /// <summary>
    /// A temperature for a map label: the number and a degree sign, no unit letter.
    ///
    /// The map draws through a fixed-cell glyph atlas with wide tracking, so "73 °F" sprawls
    /// to nearly twice the width of "73°" and starts colliding with the place names baked into
    /// the basemap. Dropping the letter halves it. Which scale is meant is said once, by
    /// whatever draws the labels, rather than repeated on every one of them — see
    /// <see cref="TemperatureUnit"/>.
    /// </summary>
    public static string TemperatureShort(double celsius) => System switch
    {
        UnitSystem.Metric => $"{Whole(celsius)}°",
        _ => $"{Whole(celsius * 9.0 / 5.0 + 32.0)}°",
    };

    /// <summary>The scale in force, for a caption that has to name it once.</summary>
    public static string TemperatureUnit => System == UnitSystem.Metric ? "°C" : "°F";

    /// <summary>
    /// Rainfall depth. Two decimals in inches and one in millimetres, because the useful
    /// resolutions differ by an order of magnitude: 0.01 in is the tipping-bucket increment
    /// most gauges actually report, and 0.3 mm is the same quantity.
    /// </summary>
    public static string Rainfall(double millimetres) => System switch
    {
        UnitSystem.Metric => $"{millimetres:F1} mm",
        _ => $"{millimetres / 25.4:F2} in",
    };

    /// <summary>
    /// A whole number of degrees, without a negative zero. .NET's formatting is IEEE-correct,
    /// so −0.4 rounds to −0 and prints as "-0" — which reads as a bug rather than as the
    /// half-degree below freezing it is. Both scales cross zero in ordinary weather, so both
    /// go through here.
    /// </summary>
    private static string Whole(double degrees)
    {
        var text = degrees.ToString("F0");
        // Formatting the comparison rather than testing for the literal "-0" keeps this
        // right in a culture that spells its negative sign differently, and rounding is left
        // to the same "F0" as before so no other reading changes.
        return text == (-0.0).ToString("F0") ? 0.0.ToString("F0") : text;
    }

    /// <summary>
    /// Station pressure. Inches of mercury is the US surface convention and is what an
    /// altimeter setting is quoted in; hectopascals are the same number as millibars.
    /// </summary>
    public static string Pressure(double pascals) => System switch
    {
        UnitSystem.Metric => $"{pascals / 100.0:F0} hPa",
        _ => $"{pascals * 0.000295299830714:F2} inHg",
    };
}
