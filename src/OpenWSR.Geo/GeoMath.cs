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

    private static double NormalizeLonDeg(double lonDeg)
    {
        lonDeg %= 360.0;
        if (lonDeg > 180.0) lonDeg -= 360.0;
        else if (lonDeg < -180.0) lonDeg += 360.0;
        return lonDeg;
    }
}
