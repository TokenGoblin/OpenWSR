namespace OpenWSR.Dashboard;

/// <summary>
/// Everything the dashboard page draws, as one immutable value.
///
/// The shell builds it on the UI thread and hands it over whole; request threads only ever
/// read the reference they were given. Nothing in here points back into a controller, so a
/// request can never observe a list half-way through being replaced.
///
/// Positions are <c>[lat, lon]</c> pairs, which is the order Leaflet takes them in.
/// </summary>
/// <param name="AlertsUpdatedUtc">
/// When the warnings poll last succeeded, or null before its first answer. Carried separately
/// from the storm clock so the page can say which half is stale: a failed warnings fetch must
/// never read as "no warnings".
/// </param>
/// <param name="StormsUpdatedUtc">
/// When the storm poll last delivered, or null when no storm watch is armed — which is the
/// normal state with no place saved, not a failure.
/// </param>
/// <param name="StormSite">The radar the storm cells come from, e.g. "KTLX".</param>
public sealed record DashboardSnapshot(
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? AlertsUpdatedUtc,
    DateTimeOffset? StormsUpdatedUtc,
    string? StormSite,
    IReadOnlyList<DashboardPlace> Places,
    IReadOnlyList<DashboardThreat> Threats,
    IReadOnlyList<DashboardAlert> Alerts,
    IReadOnlyList<DashboardStorm> Storms)
{
    /// <summary>What the page shows before the shell has published anything.</summary>
    public static DashboardSnapshot Empty { get; } =
        new(DateTimeOffset.UnixEpoch, null, null, null, [], [], [], []);
}

/// <summary>A watched place and the ring the alerting draws around it.</summary>
public sealed record DashboardPlace(
    string Name, double Lat, double Lon, double RadiusKm, bool IsPrimary);

/// <summary>
/// One row of the APPROACHING list. The text fields arrive already formatted in the user's
/// units, so the page reads exactly as the app's own panel does rather than re-deriving it.
/// </summary>
/// <param name="Rank">
/// <c>tornadic</c>, <c>overhead</c>, <c>direct</c>, <c>nearby</c> or <c>glancing</c>.
/// </param>
/// <param name="Interrupts">
/// Whether the app would raise an alert for it. A glancing pass is listed but is not an alarm.
/// </param>
public sealed record DashboardThreat(
    string Key,
    string Title,
    string Label,
    string Detail,
    string Range,
    string Rank,
    bool Interrupts,
    double DistanceKm,
    double? EtaMinutes,
    double Lat,
    double Lon,
    DateTimeOffset? Expires,
    string? PlaceName);

/// <summary>An NWS warning polygon. <paramref name="Color"/> is <c>#rrggbb</c>, NWS convention.</summary>
public sealed record DashboardAlert(
    string Id,
    string Event,
    string Headline,
    string Severity,
    DateTimeOffset? Expires,
    string Color,
    IReadOnlyList<double[][]> Polygons);

/// <summary>A SCIT storm cell with its track and hail/rotation attributes.</summary>
/// <param name="Motion">
/// Direction of travel and speed in the user's units, "NE 35 mph", or null when the cell is
/// not tracked yet — which is not the same claim as stationary.
/// </param>
public sealed record DashboardStorm(
    string Id,
    double Lat,
    double Lon,
    IReadOnlyList<double[]> ForecastPath,
    IReadOnlyList<double[]> PastPath,
    string? Motion,
    int ProbabilityOfHail,
    int ProbabilityOfSevereHail,
    int MaxHailSizeInches,
    double? MesoRadiusKm);
