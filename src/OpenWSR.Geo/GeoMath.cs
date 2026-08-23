namespace OpenWSR.Geo;

public static class GeoMath
{
    /// <summary>Mean earth radius (IUGG), meters — used for beam propagation and geodesy.</summary>
    public const double EarthRadiusM = 6371008.8;

    /// <summary>4/3 effective earth radius for standard atmospheric refraction.</summary>
    public const double EffectiveEarthRadiusM = EarthRadiusM * 4.0 / 3.0;

    /// <summary>WGS84 semi-major axis, meters — the sphere radius of Web Mercator (EPSG:3857).</summary>
    public const double MercatorRadiusM = 6378137.0;

    public const double MercatorExtentM = Math.PI * MercatorRadiusM; // ±20037508.34

    /// <summary>
    /// 4/3 effective earth radius beam propagation (Doviak &amp; Zrnić):
    /// height above the radar and ground (arc) range for a given slant range and elevation.
    /// </summary>
    public static (double GroundRangeM, double HeightM) BeamPath(double slantRangeM, double elevationRad)
    {
        double ka = EffectiveEarthRadiusM;
        double h = Math.Sqrt(slantRangeM * slantRangeM + ka * ka
                             + 2.0 * slantRangeM * ka * Math.Sin(elevationRad)) - ka;
        double s = ka * Math.Asin(slantRangeM * Math.Cos(elevationRad) / (ka + h));
        return (s, h);
    }

    /// <summary>
    /// The inverse of <see cref="BeamPath"/>: the slant range at which a beam of this
    /// elevation reaches a given height above the radar. Solving the 4/3-earth relation
    /// directly beats stepping gates until one is close enough, and it is what a wind
    /// profile needs — it works in heights, while the radar works in ranges.
    /// </summary>
    public static double SlantRangeForHeight(double heightM, double elevationRad)
    {
        double ka = EffectiveEarthRadiusM;
        double sin = Math.Sin(elevationRad), cos = Math.Cos(elevationRad);
        double inner = (heightM + ka) * (heightM + ka) - ka * ka * cos * cos;
        return inner <= 0 ? 0 : Math.Sqrt(inner) - ka * sin;
    }

    /// <summary>
    /// The other inverse of <see cref="BeamPath"/>: which beam passes through a point given
    /// as a ground (arc) range and a height above the radar, and how far along that beam the
    /// point lies. <see cref="SlantRangeForHeight"/> answers "how far along *this* beam";
    /// this answers "*which* beam", which is what any product working in a Cartesian frame —
    /// a cross-section, a volume grid — has to ask for every cell it fills.
    ///
    /// Exact on the 4/3 earth, by the triangle between the earth's centre, the radar, and
    /// the point. A negative elevation is a real answer, not an error: it is what a point
    /// below the radar horizon requires.
    /// </summary>
    public static (double ElevationRad, double SlantRangeM) BeamAngleTo(
        double groundRangeM, double heightM)
    {
        double ka = EffectiveEarthRadiusM;
        double phi = groundRangeM / ka;             // angle subtended at the earth's centre
        double horizontal = (ka + heightM) * Math.Sin(phi);
        double vertical = (ka + heightM) * Math.Cos(phi) - ka;
        return (Math.Atan2(vertical, horizontal),
                Math.Sqrt(horizontal * horizontal + vertical * vertical));
    }

    /// <summary>
    /// Spherical great-circle destination point: from (lat0, lon0), travel
    /// <paramref name="groundRangeM"/> along the initial azimuth. Degrees in, degrees out.
    /// </summary>
    public static (double LatDeg, double LonDeg) Offset(
        double lat0Deg, double lon0Deg, double azimuthRad, double groundRangeM)
    {
        double lat0 = lat0Deg * Math.PI / 180.0;
        double lon0 = lon0Deg * Math.PI / 180.0;
        double delta = groundRangeM / EarthRadiusM;

        double sinLat = Math.Sin(lat0) * Math.Cos(delta)
                        + Math.Cos(lat0) * Math.Sin(delta) * Math.Cos(azimuthRad);
        double lat = Math.Asin(sinLat);
        double lon = lon0 + Math.Atan2(
            Math.Sin(azimuthRad) * Math.Sin(delta) * Math.Cos(lat0),
            Math.Cos(delta) - Math.Sin(lat0) * sinLat);

        return (lat * 180.0 / Math.PI, NormalizeLonDeg(lon * 180.0 / Math.PI));
    }

    /// <summary>Web Mercator (EPSG:3857) projection, meters.</summary>
    public static (double X, double Y) ToMercator(double latDeg, double lonDeg)
    {
        double x = MercatorRadiusM * lonDeg * Math.PI / 180.0;
        double lat = latDeg * Math.PI / 180.0;
        double y = MercatorRadiusM * Math.Log(Math.Tan(Math.PI / 4.0 + lat / 2.0));
        return (x, y);
    }

    public static (double LatDeg, double LonDeg) FromMercator(double x, double y)
    {
        double lon = x / MercatorRadiusM * 180.0 / Math.PI;
        double lat = (2.0 * Math.Atan(Math.Exp(y / MercatorRadiusM)) - Math.PI / 2.0) * 180.0 / Math.PI;
        return (lat, lon);
    }

    /// <summary>Great-circle distance between two points, meters.</summary>
    public static double DistanceM(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        double lat1 = lat1Deg * Math.PI / 180.0, lat2 = lat2Deg * Math.PI / 180.0;
        double dLat = lat2 - lat1;
        double dLon = (lon2Deg - lon1Deg) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                   + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2.0 * EarthRadiusM * Math.Asin(Math.Sqrt(a));
    }

    /// <summary>Initial bearing from point 1 to point 2, radians clockwise from north.</summary>
    public static double BearingRad(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        double lat1 = lat1Deg * Math.PI / 180.0, lat2 = lat2Deg * Math.PI / 180.0;
        double dLon = (lon2Deg - lon1Deg) * Math.PI / 180.0;
        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return Math.Atan2(y, x);
    }

    /// <summary>Even-odd ray-cast point-in-polygon over (lat, lon) vertices.</summary>
    public static bool PointInRing(
        double latDeg, double lonDeg, IReadOnlyList<(double LatDeg, double LonDeg)> ring)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (yi, xi) = ring[i];
            var (yj, xj) = ring[j];
            if (yi > latDeg != yj > latDeg &&
                lonDeg < (xj - xi) * (latDeg - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Distance from a point to a polygon ring, measured to its <em>edges</em>. NWS warning
    /// polygons carry four to eight vertices, so the nearest edge is routinely far closer
    /// than the nearest corner — measuring to vertices misses storms passing alongside a
    /// long edge. Returns 0 when the point is inside the ring.
    /// </summary>
    public static double DistanceToRingM(
        double latDeg, double lonDeg, IReadOnlyList<(double LatDeg, double LonDeg)> ring)
    {
        if (ring.Count == 0) return double.MaxValue;
        if (ring.Count < 3) return DistanceM(latDeg, lonDeg, ring[0].LatDeg, ring[0].LonDeg);
        if (PointInRing(latDeg, lonDeg, ring)) return 0;

        double best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            best = Math.Min(best, DistanceToSegmentM(latDeg, lonDeg, ring[j], ring[i]));
        return best;
    }

    /// <summary>
    /// Distance from a point to a great-circle segment. The closest point is solved on a
    /// local equirectangular plane about the query point — exact enough over the tens of
    /// kilometres a warning polygon spans — but the returned distance is the real
    /// great-circle one, so it stays consistent with <see cref="DistanceM"/>.
    /// </summary>
    public static double DistanceToSegmentM(
        double latDeg, double lonDeg,
        (double LatDeg, double LonDeg) a, (double LatDeg, double LonDeg) b)
    {
        double cosLat = Math.Cos(latDeg * Math.PI / 180.0);
        double ax = (a.LonDeg - lonDeg) * cosLat, ay = a.LatDeg - latDeg;
        double bx = (b.LonDeg - lonDeg) * cosLat, by = b.LatDeg - latDeg;
        double dx = bx - ax, dy = by - ay;

        double lengthSq = dx * dx + dy * dy;
        double t = lengthSq <= 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / lengthSq, 0, 1);
        return DistanceM(latDeg, lonDeg,
            a.LatDeg + (b.LatDeg - a.LatDeg) * t,
            a.LonDeg + (b.LonDeg - a.LonDeg) * t);
    }

    /// <summary>
    /// What a timed path does relative to a point.
    ///
    /// <paramref name="DistanceKm"/> is the closest it ever gets and <paramref name="EtaMinutes"/>
    /// when — but those two alone cannot tell "arrives overhead in twenty minutes" from
    /// "is as close as it will ever be, and leaving". Both report the same pair when the
    /// closest approach is the current position. <paramref name="CurrentKm"/> and
    /// <paramref name="FinalKm"/> are what separate them.
    /// </summary>
    /// <param name="LatDeg">Where on the ground the closest approach happens, for naming the side it passes.</param>
    public readonly record struct PathApproach(
        double DistanceKm,
        double EtaMinutes,
        double CurrentKm,
        double FinalKm,
        double LatDeg,
        double LonDeg)
    {
        /// <summary>
        /// The track never brings it closer than it already is, and it ends further away.
        /// Nothing here is coming for the point — it is going.
        /// </summary>
        public bool IsReceding => EtaMinutes <= 0 && FinalKm > CurrentKm;

        /// <summary>
        /// The path is a single point, or every vertex sits on top of the first. SCIT emits
        /// a cell like this the first time it sees one, so this is "not tracked yet" rather
        /// than "stationary" — the same distinction <c>SpeedKmh</c> being nullable draws.
        /// </summary>
        public bool IsStationary => EtaMinutes <= 0 && Math.Abs(FinalKm - CurrentKm) < 0.25;

        /// <summary>It gets closer than it is now.</summary>
        public bool IsClosing => EtaMinutes > 0;
    }

    /// <summary>
    /// Closest approach of a timed path to a point. Path vertices are
    /// <paramref name="minutesPerSegment"/> apart (index 0 = now); segments are sampled
    /// per minute.
    /// </summary>
    public static PathApproach? ClosestApproachToPath(
        IReadOnlyList<(double LatDeg, double LonDeg)> path,
        double latDeg, double lonDeg, double minutesPerSegment = 15)
    {
        if (path.Count == 0) return null;

        double currentKm = DistanceM(latDeg, lonDeg, path[0].LatDeg, path[0].LonDeg) / 1000.0;
        double bestKm = currentKm;
        double bestMinutes = 0;
        double bestLat = path[0].LatDeg, bestLon = path[0].LonDeg;

        int steps = Math.Max(1, (int)Math.Round(minutesPerSegment));
        for (int seg = 1; seg < path.Count; seg++)
        {
            var (aLat, aLon) = path[seg - 1];
            var (bLat, bLon) = path[seg];
            for (int step = 1; step <= steps; step++)
            {
                double t = (double)step / steps;
                double lat = aLat + (bLat - aLat) * t;
                double lon = aLon + (bLon - aLon) * t;
                double d = DistanceM(latDeg, lonDeg, lat, lon) / 1000.0;
                if (d >= bestKm) continue;
                bestKm = d;
                bestMinutes = (seg - 1) * minutesPerSegment + step * minutesPerSegment / steps;
                bestLat = lat;
                bestLon = lon;
            }
        }

        var last = path[^1];
        double finalKm = DistanceM(latDeg, lonDeg, last.LatDeg, last.LonDeg) / 1000.0;
        return new PathApproach(bestKm, bestMinutes, currentKm, finalKm, bestLat, bestLon);
    }

    private static double NormalizeLonDeg(double lonDeg)
    {
        lonDeg %= 360.0;
        if (lonDeg > 180.0) lonDeg -= 360.0;
        else if (lonDeg < -180.0) lonDeg += 360.0;
        return lonDeg;
    }
}
