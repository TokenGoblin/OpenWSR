using OpenWSR.Geo;
using OpenWSR.Ingest;

namespace OpenWSR.App;

public sealed record Threat(string Key, string Title, string Detail, bool IsTornado);

/// <summary>
/// Watches storm tracks and warning polygons against the user's home location.
/// A storm threatens when its current-through-forecast path passes within the alert
/// radius (forecast points are 15 minutes apart, so an ETA falls out of where the
/// path first enters the circle). A warning threatens when its polygon contains home
/// or comes within the radius. Each threat alerts once per hour per source.
/// </summary>
public sealed class ThreatMonitor
{
    private readonly Dictionary<string, DateTime> _alerted = [];

    public double? HomeLatDeg { get; private set; }
    public double? HomeLonDeg { get; private set; }
    public double RadiusKm { get; private set; } = 40;

    public bool IsArmed => HomeLatDeg is not null;

    public event Action<Threat>? ThreatDetected;

    public void Configure(double? homeLatDeg, double? homeLonDeg, double radiusKm)
    {
        bool moved = homeLatDeg != HomeLatDeg || homeLonDeg != HomeLonDeg || radiusKm != RadiusKm;
        HomeLatDeg = homeLatDeg;
        HomeLonDeg = homeLonDeg;
        RadiusKm = radiusKm;
        if (moved) _alerted.Clear(); // new home/radius: re-evaluate everything fresh
    }

    public void EvaluateStorms(IReadOnlyList<TrackedStorm> storms)
    {
        if (HomeLatDeg is not { } homeLat || HomeLonDeg is not { } homeLon) return;

        foreach (var storm in storms)
        {
            var approach = ClosestApproach(storm, homeLat, homeLon);
            if (approach is not { } a || a.DistanceKm > RadiusKm) continue;

            string when = a.EtaMinutes <= 0
                ? "now"
                : $"in ~{a.EtaMinutes:F0} min";
            var extras = new List<string>();
            if (storm.ProbabilityOfSevereHail > 0)
                extras.Add($"severe hail {storm.ProbabilityOfSevereHail}%");
            else if (storm.ProbabilityOfHail > 0)
                extras.Add($"hail {storm.ProbabilityOfHail}%");
            if (storm.MaxHailSizeInches > 0)
                extras.Add($"{storm.MaxHailSizeInches}\" max");
            if (storm.MesoRadiusKm is not null)
                extras.Add("MESOCYCLONE");

            Raise(new Threat(
                $"storm:{storm.Id}",
                $"Storm {storm.Id} approaching your area",
                $"Track passes within {Units.Distance(a.DistanceKm)} of home {when} " +
                (storm.SpeedKmh is { } kmh && storm.BearingDeg is { } deg
                    ? $"(moving {CompassPoint(deg)} at {Units.Speed(kmh)}"
                    : "(motion not tracked yet") +
                (extras.Count > 0 ? $"; {string.Join(", ", extras)})" : ")"),
                storm.MesoRadiusKm is not null));
        }
    }

    public void EvaluateWarnings(IReadOnlyList<ActiveAlert> alerts)
    {
        if (HomeLatDeg is not { } homeLat || HomeLonDeg is not { } homeLon) return;

        foreach (var alert in alerts)
        {
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
            Raise(new Threat(
                $"warn:{alert.Id}",
                inside ? $"{alert.Event.ToUpperInvariant()} INCLUDES YOUR AREA" : alert.Event,
                (inside ? alert.Headline : $"{Units.Distance(nearestKm)} from home: {alert.Headline}") + until,
                alert.Event == "Tornado Warning"));
        }
    }

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
