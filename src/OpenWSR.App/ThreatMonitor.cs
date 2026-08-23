using OpenWSR.Geo;
using OpenWSR.Ingest;

namespace OpenWSR.App;

/// <summary>
/// How loudly a threat should read. The tiers are deliberately coarse — a list sorted by a
/// continuous score reshuffles on every refresh and stops being scannable — and within a tier
/// the ordering is soonest, then nearest.
/// </summary>
public enum ThreatRank
{
    /// <summary>Tornado warning, or a cell with a detected mesocyclone.</summary>
    Tornadic = 0,

    /// <summary>A warning polygon that contains home. It is happening where you are.</summary>
    Overhead = 1,

    /// <summary>Inside the alert radius and closing.</summary>
    Approaching = 2,
}

/// <summary>
/// One thing worth knowing about, in a form both the tray notification and the side list can
/// use. <paramref name="Key"/> is stable across refreshes — "storm:A1", "warn:{id}" — so the
/// list can be replaced wholesale every cycle without the UI losing its place or re-alerting.
/// </summary>
/// <param name="Title">
/// The sentence a tray balloon opens with: "Storm Y5 approaching your area".
/// </param>
/// <param name="Label">
/// The same thing named in two or three words, for the side list. The panel heading already
/// says APPROACHING, and repeating it down every row of a 248 px column leaves no room for
/// the range — which is the part being scanned for.
/// </param>
/// <param name="EtaMinutes">
/// Minutes until closest approach, or null when the source cannot say — a warning polygon is
/// a footprint, not a track, and reporting "0 min" for one would be inventing an arrival time.
/// </param>
public sealed record Threat(
    string Key,
    string Title,
    string Label,
    string Detail,
    ThreatRank Rank,
    double DistanceKm,
    double? EtaMinutes,
    double LatDeg,
    double LonDeg,
    DateTimeOffset? Expires = null)
{
    public bool IsTornado => Rank == ThreatRank.Tornadic;

    /// <summary>The one-line range readout for the list: "12 mi · ~18 min", or "over you".</summary>
    public string Range =>
        Rank == ThreatRank.Overhead ? "over you"
        : EtaMinutes is { } eta
            ? $"{Units.Distance(DistanceKm)} · {(eta <= 1 ? "now" : $"~{eta:F0} min")}"
            : Units.Distance(DistanceKm);
}

/// <summary>
/// Watches storm tracks and warning polygons against the user's home location.
/// A storm threatens when its current-through-forecast path passes within the alert
/// radius (forecast points are 15 minutes apart, so an ETA falls out of where the
/// path first enters the circle). A warning threatens when its polygon contains home
/// or comes within the radius.
///
/// There are two outputs and they answer different questions. <see cref="ThreatDetected"/>
/// fires once per hour per source and is what interrupts the user — a tray balloon and a
/// sound. <see cref="ThreatsChanged"/> carries the whole current set every time either half
/// is re-evaluated, and is what the side list draws; a threat that is still there on the next
/// refresh has to stay on that list without alerting again, which is why the throttle sits on
/// the notification and not on the set.
///
/// The two halves refresh on different clocks — storms with the Level III poll, warnings with
/// the api.weather.gov one — so each evaluation replaces only its own half.
/// </summary>
public sealed class ThreatMonitor
{
    private readonly Dictionary<string, DateTime> _alerted = [];
    private IReadOnlyList<Threat> _stormThreats = [];
    private IReadOnlyList<Threat> _warningThreats = [];

    public double? HomeLatDeg { get; private set; }
    public double? HomeLonDeg { get; private set; }
    public double RadiusKm { get; private set; } = 40;

    public bool IsArmed => HomeLatDeg is not null;

    /// <summary>Everything currently threatening home, most urgent first.</summary>
    public IReadOnlyList<Threat> Current { get; private set; } = [];

    /// <summary>Fires once per hour per source: the interrupting notification.</summary>
    public event Action<Threat>? ThreatDetected;

    /// <summary>Fires whenever the current set changes: the side list.</summary>
    public event Action<IReadOnlyList<Threat>>? ThreatsChanged;

    public void Configure(double? homeLatDeg, double? homeLonDeg, double radiusKm)
    {
        bool moved = homeLatDeg != HomeLatDeg || homeLonDeg != HomeLonDeg || radiusKm != RadiusKm;
        HomeLatDeg = homeLatDeg;
        HomeLonDeg = homeLonDeg;
        RadiusKm = radiusKm;
        if (!moved) return;

        _alerted.Clear(); // new home/radius: re-evaluate everything fresh
        // Every distance in the old set was measured against the old home and is now wrong.
        // Blank it rather than leave stale ranges on screen until the next poll lands.
        _stormThreats = [];
        _warningThreats = [];
        Publish();
    }

    public void EvaluateStorms(IReadOnlyList<TrackedStorm> storms)
    {
        if (HomeLatDeg is not { } homeLat || HomeLonDeg is not { } homeLon)
        {
            ReplaceStorms([]);
            return;
        }

        var found = new List<Threat>();
        foreach (var storm in storms)
        {
            var approach = ClosestApproach(storm, homeLat, homeLon);
            if (approach is not { } a || a.DistanceKm > RadiusKm) continue;

            string when = a.EtaMinutes <= 0 ? "now" : $"in ~{a.EtaMinutes:F0} min";
            var extras = new List<string>();
            if (storm.ProbabilityOfSevereHail > 0)
                extras.Add($"severe hail {storm.ProbabilityOfSevereHail}%");
            else if (storm.ProbabilityOfHail > 0)
                extras.Add($"hail {storm.ProbabilityOfHail}%");
            if (storm.MaxHailSizeInches > 0)
                extras.Add($"{storm.MaxHailSizeInches}\" max");
            if (storm.MesoRadiusKm is not null)
                extras.Add("MESOCYCLONE");

            found.Add(new Threat(
                $"storm:{storm.Id}",
                $"Storm {storm.Id} approaching your area",
                storm.MesoRadiusKm is not null ? $"Storm {storm.Id} — rotating" : $"Storm {storm.Id}",
                $"Track passes within {Units.Distance(a.DistanceKm)} of home {when} " +
                (storm.SpeedKmh is { } kmh && storm.BearingDeg is { } deg
                    ? $"(moving {CompassPoint(deg)} at {Units.Speed(kmh)}"
                    : "(motion not tracked yet") +
                (extras.Count > 0 ? $"; {string.Join(", ", extras)})" : ")"),
                storm.MesoRadiusKm is not null ? ThreatRank.Tornadic : ThreatRank.Approaching,
                a.DistanceKm,
                a.EtaMinutes,
                storm.LatDeg,
                storm.LonDeg));
        }

        ReplaceStorms(found);
        foreach (var threat in found) Raise(threat);
    }

    public void EvaluateWarnings(IReadOnlyList<ActiveAlert> alerts)
    {
        if (HomeLatDeg is not { } homeLat || HomeLonDeg is not { } homeLon)
        {
            ReplaceWarnings([]);
            return;
        }

        var found = new List<Threat>();
        foreach (var alert in alerts)
        {
            if (!IsDangerous(alert)) continue;

            // Distance to the polygon's edges, not its corners: a warning whose nearest
            // side runs 5 km from home can have its nearest vertex 60 km away.
            double nearestKm = alert.Polygons
                .Select(ring => GeoMath.DistanceToRingM(homeLat, homeLon, ring) / 1000.0)
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            bool inside = nearestKm <= 0;
            if (!inside && nearestKm > RadiusKm) continue;

            string until = alert.Expires is { } expires
                ? $" — until {expires.ToLocalTime():HH:mm}"
                : "";
            var (latDeg, lonDeg) = LookAt(alert, inside, homeLat, homeLon);
            found.Add(new Threat(
                $"warn:{alert.Id}",
                inside ? $"{alert.Event.ToUpperInvariant()} INCLUDES YOUR AREA" : alert.Event,
                alert.Event,
                (inside ? alert.Headline : $"{Units.Distance(nearestKm)} from home: {alert.Headline}") + until,
                alert.Event == "Tornado Warning" ? ThreatRank.Tornadic
                    : inside ? ThreatRank.Overhead
                    : ThreatRank.Approaching,
                Math.Max(nearestKm, 0),
                EtaMinutes: null,
                latDeg,
                lonDeg,
                alert.Expires));
        }

        ReplaceWarnings(found);
        foreach (var threat in found) Raise(threat);
    }

    /// <summary>
    /// Drops warnings whose end time has passed. The alerts poll already does this, but it
    /// runs once a minute and an expired warning left sitting in the list reads as a live one.
    /// </summary>
    public void ExpireStale(DateTimeOffset nowUtc)
    {
        var live = _warningThreats.Where(t => t.Expires is not { } e || e > nowUtc).ToList();
        if (live.Count == _warningThreats.Count) return;
        ReplaceWarnings(live);
    }

    /// <summary>
    /// Whether an alert is the kind of thing worth interrupting someone over.
    ///
    /// The warnings layer draws everything the user has ticked, advisories and statements
    /// included, and that is right for a map — but "Special Weather Statement, 49 miles away"
    /// at the top of a threat list is noise sitting where a tornado warning goes. Warnings
    /// always count; anything else has to carry a Severe or Extreme severity to. Severity is
    /// checked case-insensitively because the CAP feed has not always been consistent about it.
    /// </summary>
    private static bool IsDangerous(ActiveAlert alert) =>
        alert.Event.EndsWith("Warning", StringComparison.OrdinalIgnoreCase)
        || alert.Severity.Equals("Extreme", StringComparison.OrdinalIgnoreCase)
        || alert.Severity.Equals("Severe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Closest approach of the storm path (current → forecast) to a point, with ETA.</summary>
    internal static (double DistanceKm, double EtaMinutes)? ClosestApproach(
        TrackedStorm storm, double latDeg, double lonDeg)
    {
        var path = new List<(double LatDeg, double LonDeg)> { (storm.LatDeg, storm.LonDeg) };
        path.AddRange(storm.ForecastPath);
        return GeoMath.ClosestApproachToPath(path, latDeg, lonDeg);
    }

    internal static string CompassPoint(double bearingDeg)
    {
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return points[(int)Math.Round(bearingDeg / 45.0) % 8];
    }

    /// <summary>
    /// Where to put the camera for a warning. Home itself when the polygon contains it — the
    /// point of the jump is to show what is over you, and the far corner of a county-sized
    /// box is not that — otherwise the mean of the first ring, which lands inside any
    /// warning polygon convex enough to be one.
    /// </summary>
    private static (double LatDeg, double LonDeg) LookAt(
        ActiveAlert alert, bool inside, double homeLat, double homeLon)
    {
        if (inside) return (homeLat, homeLon);
        var ring = alert.Polygons.FirstOrDefault();
        return ring is null || ring.Count == 0
            ? (homeLat, homeLon)
            : (ring.Average(p => p.LatDeg), ring.Average(p => p.LonDeg));
    }

    private void ReplaceStorms(IReadOnlyList<Threat> threats)
    {
        _stormThreats = threats;
        Publish();
    }

    private void ReplaceWarnings(IReadOnlyList<Threat> threats)
    {
        _warningThreats = threats;
        Publish();
    }

    /// <summary>
    /// Within a tier the ordering is nearest first, and ETA only breaks ties.
    ///
    /// Soonest-first was tried and reads wrong. A cell whose closest approach is its current
    /// position reports an ETA of zero — it is at its nearest now, and may be receding — so
    /// every such cell sorted above a storm closing to 14 miles in nineteen minutes, and a
    /// forty-nine-mile warning with no track at all sorted above it too. "How close does this
    /// get to me" is the question the list answers; when it gets there is the detail.
    /// </summary>
    private void Publish()
    {
        Current = _stormThreats
            .Concat(_warningThreats)
            .OrderBy(t => (int)t.Rank)
            .ThenBy(t => t.DistanceKm)
            .ThenBy(t => t.EtaMinutes ?? 0)
            .ToList();
        ThreatsChanged?.Invoke(Current);
    }

    private void Raise(Threat threat)
    {
        var now = DateTime.UtcNow;
        foreach (var stale in _alerted.Where(kv => now - kv.Value > TimeSpan.FromHours(2))
                     .Select(kv => kv.Key).ToList())
            _alerted.Remove(stale);
        if (_alerted.TryGetValue(threat.Key, out var last) && now - last < TimeSpan.FromHours(1))
            return;
        _alerted[threat.Key] = now;
        ThreatDetected?.Invoke(threat);
    }
}
