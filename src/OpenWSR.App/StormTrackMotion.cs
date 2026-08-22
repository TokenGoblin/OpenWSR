using OpenWSR.Geo;
using OpenWSR.Nexrad;

namespace OpenWSR.App;

/// <summary>
/// Storm speed and bearing from a SCIT track.
///
/// Separate from the controller because it is arithmetic over two points and a clock, and
/// because getting it wrong is invisible: a bad time base produces a plausible number rather
/// than an error, and there is nothing on screen to check it against.
/// </summary>
public static class StormTrackMotion
{
    /// <summary>
    /// The forecast track's first step is 15 minutes out; that is the product's definition
    /// and not something to derive.
    /// </summary>
    private const double ForecastStepMinutes = 15.0;

    /// <summary>
    /// A cell's centroid is reported on a quarter-kilometre grid, so a leg shorter than one
    /// of those steps is rounding rather than movement and carries no usable direction.
    /// </summary>
    private const double MinLegMeters = 250;

    /// <summary>
    /// Motion for a cell, or null when its track cannot supply any.
    ///
    /// The forecast leg is preferred and the past leg is the fallback, and the two are on
    /// different clocks: the forecast step is a fixed 15 minutes, while consecutive past
    /// positions are one volume scan apart, which depends on the VCP. Measured against the
    /// forecast leg on 24 tracked cells across four sites, reading the past leg as though it
    /// were 15 minutes reported a median of 0.30 of the real speed — 4.5 minutes, exactly the
    /// VCP 12/212 scan time.
    /// </summary>
    public static (double SpeedKmh, double BearingDeg)? Derive(
        (double LatDeg, double LonDeg) current,
        IReadOnlyList<(double LatDeg, double LonDeg)> forecast,
        IReadOnlyList<(double LatDeg, double LonDeg)> past,
        int vcp) =>
        FromLeg(current, forecast, ForecastStepMinutes, toward: true)
        ?? FromLeg(current, past, VolumeCoveragePattern.NominalScanMinutes(vcp), toward: false);

    /// <summary>
    /// Speed and bearing from one leg of a track, or null when there is no leg to take them
    /// from. <paramref name="toward"/> is false for a past position, where the motion points
    /// away from the reference rather than at it.
    ///
    /// A null here is the point of the whole exercise. The algorithm emits a cell the first
    /// time it sees one, with a forecast point sitting exactly on the current position; that
    /// is a storm whose motion is unknown, not a storm measured to be standing still, and the
    /// two should not read the same on screen.
    /// </summary>
    private static (double SpeedKmh, double BearingDeg)? FromLeg(
        (double LatDeg, double LonDeg) current,
        IReadOnlyList<(double LatDeg, double LonDeg)> track,
        double legMinutes, bool toward)
    {
        if (track.Count == 0 || legMinutes <= 0) return null;

        var other = track[0];
        double meters = GeoMath.DistanceM(current.LatDeg, current.LonDeg, other.LatDeg, other.LonDeg);
        if (meters < MinLegMeters) return null;

        double bearing =
            GeoMath.BearingRad(current.LatDeg, current.LonDeg, other.LatDeg, other.LonDeg)
            * 180.0 / Math.PI;
        if (!toward) bearing += 180.0;

        return (meters / 1000.0 * (60.0 / legMinutes), (bearing + 360.0) % 360.0);
    }
}
