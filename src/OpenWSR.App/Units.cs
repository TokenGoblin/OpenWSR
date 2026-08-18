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

    /// <summary>Beam or echo height. Metric stays in metres; the others use feet.</summary>
    public static string Height(double metres) => System switch
    {
        UnitSystem.Metric => $"{metres:F0} m",
        _ => $"{metres * 3.28084:F0} ft",
    };

    public static string HeightKft(double kilofeet) => System switch
    {
        UnitSystem.Metric => $"{kilofeet * 304.8:F0} m",
        _ => $"{kilofeet:F0} kft",
    };
}
