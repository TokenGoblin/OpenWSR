using OpenWSR.Geo;
using OpenWSR.Ingest;

namespace OpenWSR.App;

/// <summary>
/// One place being watched, flattened out of the settings so the monitor does not depend on
/// how they are stored or edited.
/// </summary>
public readonly record struct WatchedPlace(string Name, double LatDeg, double LonDeg, double RadiusKm);

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

    /// <summary>A storm whose track passes within the direct-hit radius, and is still closing.</summary>
    Direct = 2,

    /// <summary>A warning near home. A polygon has no track, so it cannot be judged further.</summary>
    Nearby = 3,

    /// <summary>
    /// A storm that comes inside the alert radius but misses. It is in your area and not
    /// coming to your house, which is a different sentence and deserves a quieter one.
    /// </summary>
    Glancing = 4,
}

/// <summary>
/// One thing worth knowing about, in a form both the tray notification and the side list can
/// use. <paramref name="SourceKey"/> is stable across refreshes — "storm:A1", "warn:{id}" — so
/// the list can be replaced wholesale every cycle without the UI losing its place or
/// re-alerting.
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
    string SourceKey,
    string Title,
    string Label,
    string Detail,
    ThreatRank Rank,
    double DistanceKm,
    double? EtaMinutes,
    double LatDeg,
    double LonDeg,
    DateTimeOffset? Expires = null,
    string? PassesSide = null,
    string? PlaceName = null)
{
    public bool IsTornado => Rank == ThreatRank.Tornadic;

    /// <summary>
    /// What the once-an-hour notification throttle is keyed on. It includes the place,
    /// because one storm crossing two watched locations is two things worth being told —
    /// hearing about it at home should not use up the alert for the office.
    /// </summary>
    public string Key => PlaceName is null ? SourceKey : $"{SourceKey}@{PlaceName}";

    /// <summary>
    /// Whether this is worth taking someone's attention for.
    ///
    /// A glancing pass is real and belongs on the list, but it is the answer "no" to the
    /// question the notification asks. Interrupting for it is what teaches people to ignore
    /// the notification that matters.
    /// </summary>
    public bool Interrupts => Rank != ThreatRank.Glancing;

    /// <summary>The one-line range readout for the list.</summary>
    public string Range =>
        Rank == ThreatRank.Overhead ? "over you"
        : EtaMinutes is { } eta
            ? $"{Units.Distance(DistanceKm)}{Side} · {(eta <= 1 ? "now" : $"~{eta:F0} min")}"
            : $"{Units.Distance(DistanceKm)}{Side}";

    private string Side => PassesSide is null ? "" : $" {PassesSide}";
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

    private IReadOnlyList<WatchedPlace> _places = [];

    /// <summary>The places being watched, in the order they were configured.</summary>
    public IReadOnlyList<WatchedPlace> Places => _places;

    /// <summary>
    /// The first watched place, which is the primary. Kept because the storm popup and the
    /// map marker want a single point to measure against, and the primary is that point.
    /// </summary>
    public double? HomeLatDeg => _places.Count > 0 ? _places[0].LatDeg : null;

    public double? HomeLonDeg => _places.Count > 0 ? _places[0].LonDeg : null;

    public double RadiusKm => _places.Count > 0 ? _places[0].RadiusKm : 40;

    /// <summary>
    /// How close a track has to pass to count as coming for you rather than going by.
    ///
    /// A storm is not a point — a precipitation core is kilometres across and the SCIT
    /// centroid is its middle — so this is a few kilometres rather than zero. Five miles is
    /// roughly "the storm is overhead", allowing for the forecast track being a forecast.
    /// </summary>
    public double DirectHitRadiusKm { get; private set; } = 8;

    public bool IsArmed => _places.Count > 0;

    /// <summary>Everything currently threatening home, most urgent first.</summary>
    public IReadOnlyList<Threat> Current { get; private set; } = [];

    /// <summary>Fires once per hour per source: the interrupting notification.</summary>
    public event Action<Threat>? ThreatDetected;

    /// <summary>Fires whenever the current set changes: the side list.</summary>
    public event Action<IReadOnlyList<Threat>>? ThreatsChanged;

    /// <summary>
    /// Set the places being watched. The first is the primary — the one the map marker and the
    /// storm popup measure against, both of which need a single point.
    /// </summary>
    public void Configure(IReadOnlyList<WatchedPlace> places, double directHitRadiusKm = 8)
    {
        // A direct-hit radius wider than the tightest alert radius would make every threat at
        // that place a direct hit, which is a setting that cannot mean anything. Clamped
        // against the narrowest rather than the primary's, so no place is left unable to
        // distinguish the two.
        double tightest = places.Count > 0 ? places.Min(p => p.RadiusKm) : double.MaxValue;
        double clamped = Math.Clamp(directHitRadiusKm, 0, tightest);

        bool changed = clamped != DirectHitRadiusKm || !places.SequenceEqual(_places);
        _places = [.. places];
        DirectHitRadiusKm = clamped;
        if (!changed) return;

        _alerted.Clear(); // new places or radii: re-evaluate everything fresh
        // Every distance in the old set was measured against the old places and is now wrong.
        // Blank it rather than leave stale ranges on screen until the next poll lands.
        _stormThreats = [];
        _warningThreats = [];
        Publish();
    }

    /// <summary>Convenience for a single place, which is what most of the app still has.</summary>
    public void Configure(
        double? homeLatDeg, double? homeLonDeg, double radiusKm, double directHitRadiusKm = 8) =>
        Configure(
            homeLatDeg is { } lat && homeLonDeg is { } lon
                ? [new WatchedPlace("Home", lat, lon, radiusKm)]
                : [],
            directHitRadiusKm);

    /// <summary>
    /// Judge each tracked cell against home.
    ///
    /// The question a notification answers is "is this coming to my house", and until now
    /// the code answered a different one: "does this pass anywhere inside a fifty-mile
    /// circle". Those differ in two ways that both produced false alarms. A storm that
    /// misses by thirty-five miles read exactly like one passing overhead, and a storm
    /// already past its closest point reported an ETA of zero — rendered as "now" — because
    /// closest-approach alone cannot tell arriving from leaving.
    ///
    /// So a track now resolves to one of three things. It is closing and will pass within
    /// the direct-hit radius; it is closing and will miss; or it is going away, in which
    /// case it is not on the list at all. The panel is titled APPROACHING and a departing
    /// storm contradicts the title.
    /// </summary>
    public void EvaluateStorms(IReadOnlyList<TrackedStorm> storms)
    {
        if (_places.Count == 0)
        {
            ReplaceStorms([]);
            return;
        }

        var found = new List<Threat>();
        foreach (var place in _places)
        foreach (var storm in storms)
        {
            double homeLat = place.LatDeg, homeLon = place.LonDeg;
            if (ClosestApproach(storm, homeLat, homeLon) is not { } a) continue;
            if (a.DistanceKm > place.RadiusKm) continue;

            // Going away. Nothing about it is approaching, whatever the circle says.
            if (a.IsReceding) continue;

            // No track yet — SCIT emits a cell with its forecast sitting on its current
            // position the first time it sees one. Nothing can be said about where it is
            // headed, so it counts only when it is already on top of you; guessing in
            // either direction would be inventing a forecast.
            if (a.IsStationary && a.DistanceKm > DirectHitRadiusKm) continue;

            bool direct = a.DistanceKm <= DirectHitRadiusKm;
            bool rotating = storm.MesoRadiusKm is not null;
            string? side = direct || a.IsStationary
                ? null
                : CompassPoint(GeoMath.BearingRad(homeLat, homeLon, a.LatDeg, a.LonDeg) * 180.0 / Math.PI);

            var extras = new List<string>();
            if (storm.ProbabilityOfSevereHail > 0)
                extras.Add($"severe hail {storm.ProbabilityOfSevereHail}%");
            else if (storm.ProbabilityOfHail > 0)
                extras.Add($"hail {storm.ProbabilityOfHail}%");
            if (storm.MaxHailSizeInches > 0)
                extras.Add($"{storm.MaxHailSizeInches}\" max");
            if (rotating) extras.Add("MESOCYCLONE");

            string motion = storm.SpeedKmh is { } kmh && storm.BearingDeg is { } deg
                ? $"moving {CompassPoint(deg)} at {Units.Speed(kmh)}"
                : "motion not tracked yet";
            string when = a.EtaMinutes < 1 ? "now" : $"in ~{a.EtaMinutes:F0} min";
            string tail = extras.Count > 0 ? $"; {string.Join(", ", extras)}" : "";

            found.Add(direct
                ? new Threat(
                    $"storm:{storm.Id}",
                    $"Storm {storm.Id} is heading for you",
                    rotating ? $"Storm {storm.Id} — rotating" : $"Storm {storm.Id}",
                    $"Passes within {Units.Distance(a.DistanceKm)} of home {when} ({motion}{tail})",
                    rotating ? ThreatRank.Tornadic : ThreatRank.Direct,
                    a.DistanceKm, a.EtaMinutes, storm.LatDeg, storm.LonDeg,
                    PlaceName: place.Name)
                : new Threat(
                    $"storm:{storm.Id}",
                    $"Storm {storm.Id} passing {Units.Distance(a.DistanceKm)} to your {side}",
                    rotating ? $"Storm {storm.Id} — rotating" : $"Storm {storm.Id}",
                    $"Closest approach {Units.Distance(a.DistanceKm)} to your {side} {when} " +
                    $"({motion}{tail}). It is in your area but not on course for you.",
                    // A rotating cell still interrupts even when it misses: a mesocyclone
                    // near you is worth knowing about, and the forecast track is the part
                    // of this least worth betting on.
                    rotating ? ThreatRank.Tornadic : ThreatRank.Glancing,
                    a.DistanceKm, a.EtaMinutes, storm.LatDeg, storm.LonDeg,
                    PassesSide: side, PlaceName: place.Name));
        }

        ReplaceStorms(found);
        foreach (var threat in found)
            if (threat.Interrupts) Raise(threat);
    }

    public void EvaluateWarnings(IReadOnlyList<ActiveAlert> alerts)
    {
        if (_places.Count == 0)
        {
            ReplaceWarnings([]);
            return;
        }

        var found = new List<Threat>();
        foreach (var place in _places)
        foreach (var alert in alerts)
        {
            if (!IsDangerous(alert)) continue;
            double homeLat = place.LatDeg, homeLon = place.LonDeg;

            // Distance to the polygon's edges, not its corners: a warning whose nearest
            // side runs 5 km from home can have its nearest vertex 60 km away.
            double nearestKm = alert.Polygons
                .Select(ring => GeoMath.DistanceToRingM(homeLat, homeLon, ring) / 1000.0)
                .DefaultIfEmpty(double.MaxValue)
                .Min();
            bool inside = nearestKm <= 0;
            if (!inside && nearestKm > place.RadiusKm) continue;

            string until = alert.Expires is { } expires
                ? $" — until {expires.ToLocalTime():HH:mm}"
                : "";
            var (latDeg, lonDeg) = LookAt(alert, inside, homeLat, homeLon);
            found.Add(new Threat(
                $"warn:{alert.Id}",
                inside ? $"{alert.Event.ToUpperInvariant()} INCLUDES YOUR AREA" : alert.Event,
                alert.Event,
                (inside ? alert.Headline : $"{Units.Distance(nearestKm)} from home: {alert.Headline}") + until,
                // A polygon has no track, so there is nothing to judge it as closing or
                // going: a warning near home stays a warning near home. Only storm cells
                // get the direct/glancing distinction, because only they carry a forecast.
                alert.Event == "Tornado Warning" ? ThreatRank.Tornadic
                    : inside ? ThreatRank.Overhead
                    : ThreatRank.Nearby,
                Math.Max(nearestKm, 0),
                EtaMinutes: null,
                latDeg,
                lonDeg,
                alert.Expires,
                PlaceName: place.Name));
        }

        ReplaceWarnings(found);
        foreach (var threat in found)
            if (threat.Interrupts) Raise(threat);
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

    /// <summary>
    /// What the storm's track does relative to a point.
    ///
    /// The SCIT forecast is used when it says anything. It frequently does not: the algorithm
    /// emits a cell with its forecast points sitting on its current position, and that stays
    /// true for a while. But <c>SpeedKmh</c> and <c>BearingDeg</c> are derived from the
    /// <em>past</em> track and are often known when the forecast is still degenerate — so
    /// falling straight through to "not tracked yet" would throw away measured motion and
    /// drop storms that are demonstrably coming. Extrapolating an hour along a measured
    /// heading is what a forecast track is; doing it here is not inventing anything the
    /// cone renderer does not already do for the same reason.
    /// </summary>
    internal static GeoMath.PathApproach? ClosestApproach(
        TrackedStorm storm, double latDeg, double lonDeg)
    {
        var path = new List<(double LatDeg, double LonDeg)> { (storm.LatDeg, storm.LonDeg) };
        path.AddRange(storm.ForecastPath);

        bool forecastSaysNothing = path.Skip(1).All(p =>
            GeoMath.DistanceM(storm.LatDeg, storm.LonDeg, p.LatDeg, p.LonDeg) < 250);
        if (forecastSaysNothing &&
            storm.SpeedKmh is { } kmh && kmh > 0 && storm.BearingDeg is { } bearingDeg)
        {
            path.RemoveRange(1, path.Count - 1);
            double bearingRad = bearingDeg * Math.PI / 180.0;
            for (int quarter = 1; quarter <= 4; quarter++)   // four 15-minute steps
                path.Add(GeoMath.Offset(
                    storm.LatDeg, storm.LonDeg, bearingRad, kmh * 1000.0 * quarter / 4.0));
        }

        return GeoMath.ClosestApproachToPath(path, latDeg, lonDeg);
    }

    /// <summary>
    /// The eight-point compass name for a bearing. Wraps in both directions: a bearing
    /// straight out of <see cref="GeoMath.BearingRad"/> is an atan2 result and is negative
    /// for anything west of north, which a bare modulo turns into a negative array index.
    /// </summary>
    internal static string CompassPoint(double bearingDeg)
    {
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        int index = (int)Math.Round(bearingDeg / 45.0) % 8;
        return points[index < 0 ? index + 8 : index];
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
        var all = _stormThreats.Concat(_warningThreats);

        // Which place is threatened only needs saying when there is more than one. With a
        // single location it is the only answer there is, and putting "— Home" on every row
        // of a narrow panel spends the space that carries the range.
        if (_places.Count > 1)
            all = all.Select(t => t.PlaceName is null ? t : t with
            {
                Title = $"{t.Title} — {t.PlaceName}",
                Label = $"{t.Label} → {t.PlaceName}",
            });

        Current = all
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
