using OpenWSR.Ingest;

namespace OpenWSR.App.Tests;

/// <summary>
/// GLM object keys encode the scan start as a day-of-year timestamp. Getting it wrong
/// silently shifts the window, so recent flashes look old and fade out immediately.
/// </summary>
public class GlmClientTests
{
    [Fact]
    public void ParsesTheScanStartFromARealKey()
    {
        // The committed test file: s2026 230 12 00 00 0 -> day 230 of 2026 is 18 August.
        var t = GlmClient.StartTimeOf(
            "GLM-L2-LCFA/2026/230/12/OR_GLM-L2-LCFA_G19_s20262301200000_e20262301200200_c20262301200221.nc");

        Assert.NotNull(t);
        Assert.Equal(new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc), t!.Value);
        Assert.Equal(DateTimeKind.Utc, t.Value.Kind);
    }

    [Theory]
    [InlineData("2026", 1, 2026, 1, 1)]      // day 1 is 1 January, not 2 January
    [InlineData("2026", 230, 2026, 8, 18)]
    [InlineData("2026", 365, 2026, 12, 31)]
    [InlineData("2024", 366, 2024, 12, 31)]  // a leap year has a day 366
    public void DayOfYearMapsToTheRightCalendarDate(
        string year, int day, int expectedYear, int expectedMonth, int expectedDay)
    {
        var t = GlmClient.StartTimeOf($"OR_GLM-L2-LCFA_G19_s{year}{day:D3}0000000_e_c.nc");
        Assert.NotNull(t);
        Assert.Equal(new DateTime(expectedYear, expectedMonth, expectedDay, 0, 0, 0, DateTimeKind.Utc),
            t!.Value);
    }

    [Fact]
    public void ReadsTheTimeOfDay()
    {
        var t = GlmClient.StartTimeOf("OR_GLM-L2-LCFA_G19_s20262302345400_e_c.nc");
        Assert.Equal(new DateTime(2026, 8, 18, 23, 45, 40, DateTimeKind.Utc), t!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense.nc")]
    [InlineData("OR_GLM-L2-LCFA_G19_s2026.nc")]        // truncated
    [InlineData("OR_GLM-L2-LCFA_G19_sXXXXXXXXXXXXX.nc")] // not digits
    [InlineData("OR_GLM-L2-LCFA_G19_s20263670000000_e_c.nc")] // day 367 does not exist
    public void RubbishKeysReturnNothingRatherThanThrowing(string key) =>
        Assert.Null(GlmClient.StartTimeOf(key));
}
