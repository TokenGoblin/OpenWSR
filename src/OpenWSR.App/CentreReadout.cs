using OpenWSR.Geo;

namespace OpenWSR.App;

/// <summary>What the map-centre readout should say. <see cref="Relation"/> is empty when
/// there is no saved place to measure against.</summary>
public readonly record struct CentreDescription(string Coordinates, string Relation, bool OnPlace);

/// <summary>
/// Describes where the camera is pointed, relative to the places you have saved.
///
/// This answers a different question from the site combo beside it. That names the radar
/// feeding the screen; this names the ground under the middle of the map, and after a
/// hotspot jump, a hand pan or a pinned pane the two can be several hundred miles apart
/// with nothing on screen admitting it.
/// </summary>
public static class CentreReadout
{
    /// <summary>
    /// How near the centre a place has to be to count as centred, in screen pixels.
    ///
    /// Pixels rather than kilometres, because that is the question the eye is asking. At
    /// national zoom a place 20 km off centre sits a pixel from the middle and is centred
    /// by any honest reading; at street zoom the same 20 km is off the side of the map. A
    /// fixed ground distance would be wrong at one end or the other, and picking which end
    /// to be wrong at is not a choice worth making.
    /// </summary>
    public const double CentredWithinPixels = 12.0;

    public static CentreDescription Describe(
        double centreLatDeg, double centreLonDeg, double metresPerPixel,
        IReadOnlyList<SavedLocation> places)
    {
        string coordinates = $"{centreLatDeg:F3}, {centreLonDeg:F3}";

        // Before the first run has found a place there is nothing to be centred on. Checked
        // up front rather than trusting MinBy to hand back a default: the sequence below is
        // of value tuples, and MinBy throws on an empty one instead of returning null.
        if (places.Count == 0)
            return new CentreDescription(coordinates, "", false);

        // The nearest saved place, not the primary one: being centred on the office is just
        // as much an answer to "am I looking at the right ground" as being centred on home,
        // and with a single place saved the two are the same question.
        var nearest = places
            .Select(p => (Place: p, Km: GeoMath.DistanceM(centreLatDeg, centreLonDeg, p.LatDeg, p.LonDeg) / 1000.0))
            .MinBy(p => p.Km);

        // metresPerPixel is Mercator, and Mercator metres overstate ground metres by
        // 1/cos(latitude). Comparing a great-circle distance against it directly makes the
        // tolerance grow with latitude — 12 px becomes about 16 at 40°N and 25 at an Alaskan
        // site — so the rule would be stated in pixels and enforced in something else.
        double groundMetresPerPixel = metresPerPixel * Math.Cos(centreLatDeg * Math.PI / 180.0);
        if (nearest.Km <= CentredWithinPixels * groundMetresPerPixel / 1000.0)
            return new CentreDescription(coordinates, $"on {nearest.Place.Name}", true);

        double bearingDeg = GeoMath.BearingRad(
            nearest.Place.LatDeg, nearest.Place.LonDeg, centreLatDeg, centreLonDeg) * 180.0 / Math.PI;
        return new CentreDescription(
            coordinates,
            $"{Units.Distance(nearest.Km)} {ThreatMonitor.CompassPoint(bearingDeg)} of {nearest.Place.Name}",
            false);
    }
}
