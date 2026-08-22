namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Storm speed derived from a past track position divides by this number, so a wrong value
/// here is a wrong speed on screen rather than an error anywhere.
/// </summary>
public sealed class VolumeCoveragePatternTests
{
    [Theory]
    [InlineData(12, 4.5)]    // precipitation, the common convective choice
    [InlineData(212, 4.5)]
    [InlineData(211, 5.0)]
    [InlineData(21, 6.0)]
    [InlineData(35, 7.0)]    // clear air
    [InlineData(31, 10.0)]
    public void KnownPatternsReportTheirNominalScanTime(int vcp, double minutes) =>
        Assert.Equal(minutes, VolumeCoveragePattern.NominalScanMinutes(vcp));

    [Fact]
    public void AnUnknownPatternAssumesPrecipitationMode()
    {
        // Guessing a clear-air ten minutes for an unknown VCP would double every storm
        // speed derived through it. Storm tracking only has output in precipitation mode.
        Assert.Equal(5.0, VolumeCoveragePattern.NominalScanMinutes(999));
        Assert.InRange(VolumeCoveragePattern.NominalScanMinutes(0), 4.0, 6.0);
    }

    [Fact]
    public void ClearAirPatternsAreAlwaysSlowerThanPrecipitationOnes()
    {
        int[] precipVcps = [12, 212, 11, 211, 112, 21, 121, 215, 221];
        int[] clearAirVcps = [35, 31, 32];
        var precip = precipVcps.Select(VolumeCoveragePattern.NominalScanMinutes).ToArray();
        var clearAir = clearAirVcps.Select(VolumeCoveragePattern.NominalScanMinutes).ToArray();

        Assert.True(precip.Max() < clearAir.Min());
    }
}
