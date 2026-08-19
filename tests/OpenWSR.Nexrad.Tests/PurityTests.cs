using System.Reflection;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// The build plan requires OpenWSR.Nexrad and OpenWSR.Geo to have zero dependencies
/// on WPF, D3D, or the network. These tests enforce that at the assembly level.
/// </summary>
public class PurityTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "PresentationCore", "PresentationFramework", "WindowsBase", // WPF
        "Vortice",                                                  // D3D
        "System.Net", "AWSSDK",                                     // network
    ];

    [Theory]
    [InlineData("OpenWSR.Nexrad")]
    [InlineData("OpenWSR.Geo")]
    [InlineData("OpenWSR.Grib2")]
    [InlineData("OpenWSR.Placefiles")]
    [InlineData("OpenWSR.NetCdf")]
    public void PureAssembliesReferenceNoForbiddenDependencies(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var offenders = assembly.GetReferencedAssemblies()
            .Where(r => ForbiddenPrefixes.Any(p =>
                r.Name!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Name)
            .ToArray();

        Assert.Empty(offenders);
    }
}
