using System.Runtime.Versioning;
using Windows.Devices.Geolocation;

namespace OpenWSR.App;

/// <summary>Where a fix came from and how good it is. Accuracy is the radius the OS quotes.</summary>
public sealed record LocationFix(double LatDeg, double LonDeg, double? AccuracyM, string Source);

/// <summary>
/// One-shot lookup against Windows Location Services.
///
/// Failure here is nearly always a privacy setting rather than a bug, and the two switches
/// that block it live in different places — the machine-wide "Location services" toggle and
/// the separate "Let desktop apps access your location" one further down the same page. An
/// unpackaged desktop app gets no consent prompt when either is off; the call simply returns
/// Denied. So every failure carries the sentence that fixes it, and the caller shows that
/// rather than a status code.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public static class GeoLocationService
{
    /// <summary>
    /// The GPS-grade path can sit on a cold radio for the better part of a minute. A home
    /// location does not need metres, so ask for the fast network fix and cap the wait.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    public sealed class LocationUnavailableException(string message) : Exception(message);

    private const string PrivacyHint =
        "Open Settings › Privacy & security › Location, turn on \"Location services\", and " +
        "make sure \"Let desktop apps access your location\" is on as well.";

    /// <summary>
    /// Resolves the device's current position. Throws <see cref="LocationUnavailableException"/>
    /// with a message written for the user — never a bare status code.
    /// </summary>
    /// <remarks>
    /// <see cref="Geolocator.RequestAccessAsync"/> must be called from a UI thread: it may put
    /// up the consent dialog, and a dialog needs a message pump. Call this from the dispatcher.
    /// </remarks>
    public static async Task<LocationFix> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        GeolocationAccessStatus access;
        try
        {
            access = await Geolocator.RequestAccessAsync();
        }
        catch (Exception ex)
        {
            // Thrown outright on editions where the location service is absent or disabled
            // by policy, rather than coming back as a status.
            throw new LocationUnavailableException(
                $"Windows would not answer a location request ({ex.Message}). {PrivacyHint}");
        }

        if (access != GeolocationAccessStatus.Allowed)
            throw new LocationUnavailableException(
                access == GeolocationAccessStatus.Denied
                    ? $"Windows is not letting OpenWSR see your location. {PrivacyHint}"
                    : $"Windows has no location provider available on this PC. {PrivacyHint} " +
                      "If there is no location hardware, set your home by clicking the map instead.");

        // Whole-metre desired accuracy would demand GPS; the coarse tier is a wifi/IP fix,
        // which arrives in a second or two and is well inside any sensible alert radius.
        //
        // Constructing this is guarded too. On a machine with no location provider at all it
        // throws rather than returning a status, and this method's whole contract is that it
        // fails as a LocationUnavailableException carrying a sentence the user can act on —
        // a caller filtering on that type would otherwise see nothing and say nothing.
        Geolocator locator;
        try
        {
            locator = new Geolocator
            {
                DesiredAccuracy = PositionAccuracy.Default,
                ReportInterval = 1000,
            };
        }
        catch (Exception ex)
        {
            throw new LocationUnavailableException(
                $"Windows could not start a location request ({ex.Message}). {PrivacyHint}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        Geoposition position;
        try
        {
            position = await locator.GetGeopositionAsync().AsTask(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LocationUnavailableException(
                $"Windows did not return a fix within {Timeout.TotalSeconds:F0} seconds. This " +
                "usually means location is on but no provider has data yet — try again in a " +
                "moment, or set your home by clicking the map.");
        }
        catch (Exception ex)
        {
            throw new LocationUnavailableException(
                $"The location request failed ({ex.Message}). {PrivacyHint}");
        }

        var point = position.Coordinate?.Point
            ?? throw new LocationUnavailableException(
                "Windows returned a position with no coordinates. Set your home by clicking the map.");

        return new LocationFix(
            point.Position.Latitude,
            point.Position.Longitude,
            position.Coordinate!.Accuracy,
            Describe(position.Coordinate.PositionSource));
    }

    private static string Describe(PositionSource source) => source switch
    {
        PositionSource.Satellite => "GPS",
        PositionSource.WiFi => "Wi-Fi",
        PositionSource.Cellular => "cellular",
        PositionSource.IPAddress => "IP address",
        PositionSource.Unknown => "Windows",
        _ => "Windows",
    };
}
