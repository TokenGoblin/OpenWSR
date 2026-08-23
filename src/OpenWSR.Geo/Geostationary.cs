namespace OpenWSR.Geo;

/// <summary>
/// The GOES-R ABI fixed grid: a geostationary perspective projection.
///
/// Unlike Lambert or Mercator this is not a map projection in the usual sense — it is what
/// a camera 35,786 km up actually sees. Its coordinates are the two scan angles the
/// instrument was pointed at, in radians, and the relationship to the ground is a line-of-
/// sight intersection with the ellipsoid rather than a conformal mapping. Two consequences
/// follow and both matter here: the scale changes enormously across the disk, so a pixel
/// over Canada covers several times the ground a pixel over the equator does, and rather
/// more than half of the grid does not touch the earth at all. Both directions therefore
/// have to be able to answer "nowhere".
///
/// Equations are from the GOES-R Product Definition and Users' Guide, volume 5, section
/// 5.1.2.8.1, and the constants come from the file's own <c>goes_imager_projection</c>
/// attributes rather than from here — the satellite moves, and GOES-East has already been
/// three different spacecraft.
/// </summary>
public sealed class Geostationary
{
    private readonly double _h;          // satellite distance from the earth's centre
    private readonly double _rEq;
    private readonly double _rPol;
    private readonly double _lonOriginRad;
    private readonly bool _sweepX;
    private readonly double _ratioSquared;   // (rEq / rPol)^2
    private readonly double _eccentricitySquared;

    /// <param name="perspectivePointHeightM">
    /// Height above the ellipsoid, which is what the file stores — the equations want the
    /// distance from the earth's <em>centre</em>, so the equatorial radius is added here.
    /// Passing the file's number straight into the maths puts the satellite inside the
    /// planet and every latitude comes back wrong by degrees.
    /// </param>
    /// <param name="sweepX">
    /// Which scan angle the instrument applies first. GOES-R is 'x'; Meteosat is 'y'.
    /// Getting it backwards mirrors the image about the sub-satellite point, which near the
    /// centre of the disk looks almost right.
    /// </param>
    public Geostationary(
        double perspectivePointHeightM,
        double semiMajorM,
        double semiMinorM,
        double subSatelliteLonDeg,
        bool sweepX = true)
    {
        _rEq = semiMajorM;
        _rPol = semiMinorM;
        _h = perspectivePointHeightM + semiMajorM;
        _lonOriginRad = subSatelliteLonDeg * Math.PI / 180.0;
        _sweepX = sweepX;
        _ratioSquared = (_rEq * _rEq) / (_rPol * _rPol);
        _eccentricitySquared = (_rEq * _rEq - _rPol * _rPol) / (_rEq * _rEq);
    }

    /// <summary>The ABI fixed grid for a GOES-R satellite at the given sub-satellite longitude.</summary>
    public static Geostationary GoesR(double subSatelliteLonDeg) =>
        new(35786023.0, 6378137.0, 6356752.31414, subSatelliteLonDeg);

    /// <summary>
    /// Ground position to scan angles, in radians. Null when the point is over the horizon
    /// as seen from the satellite — which for GOES-East is everything past about 81° of arc
    /// from the sub-satellite point, so most of the planet.
    /// </summary>
    public (double X, double Y)? Forward(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180.0;
        double lon = lonDeg * Math.PI / 180.0;

        // Geodetic to geocentric latitude, then the ellipsoid radius at that latitude.
        double latC = Math.Atan(Math.Tan(lat) / _ratioSquared);
        double cosLatC = Math.Cos(latC);
        double rC = _rPol / Math.Sqrt(1.0 - _eccentricitySquared * cosLatC * cosLatC);

        double deltaLon = lon - _lonOriginRad;
        double sx = _h - rC * cosLatC * Math.Cos(deltaLon);
        double sy = -rC * cosLatC * Math.Sin(deltaLon);
        double sz = rC * Math.Sin(latC);

        // The visibility test. Without it the far side of the earth projects to perfectly
        // plausible scan angles and the imagery appears mirrored onto the wrong continent.
        if (_h * (_h - sx) <= sy * sy + _ratioSquared * sz * sz) return null;

        double r = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        return _sweepX
            ? (Math.Asin(-sy / r), Math.Atan(sz / sx))
            : (Math.Atan(-sy / sx), Math.Asin(sz / r));
    }

    /// <summary>
    /// Scan angles to ground position. Null when the line of sight misses the earth, which
    /// is the majority of any full-disk grid.
    /// </summary>
    public (double LatDeg, double LonDeg)? Inverse(double x, double y)
    {
        double sinX = Math.Sin(x), cosX = Math.Cos(x);
        double sinY = Math.Sin(y), cosY = Math.Cos(y);

        // Where the line of sight meets the ellipsoid: a quadratic with two roots, the
        // nearer of which is the visible surface.
        double a = sinX * sinX + cosX * cosX * (cosY * cosY + _ratioSquared * sinY * sinY);
        double b = -2.0 * _h * cosX * cosY;
        double c = _h * _h - _rEq * _rEq;

        double discriminant = b * b - 4.0 * a * c;
        if (discriminant < 0) return null;          // the ray passes beside the planet

        double rs = (-b - Math.Sqrt(discriminant)) / (2.0 * a);
        double sx = rs * cosX * cosY;
        double sy = -rs * sinX;
        double sz = rs * cosX * sinY;

        double lat = Math.Atan(_ratioSquared * sz / Math.Sqrt((_h - sx) * (_h - sx) + sy * sy));
        double lon = _lonOriginRad - Math.Atan(sy / (_h - sx));
        return (lat * 180.0 / Math.PI, lon * 180.0 / Math.PI);
    }
}
