namespace OpenWSR.Nexrad;

internal static class NexradTime
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>NEXRAD "modified Julian date": day 1 = 1970-01-01, plus milliseconds past midnight UTC.</summary>
    public static DateTime FromJulian(int julianDate, uint millisOfDay) =>
        Epoch.AddDays(julianDate - 1).AddMilliseconds(millisOfDay);
}
