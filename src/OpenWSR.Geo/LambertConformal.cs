namespace OpenWSR.Geo;

/// <summary>
/// Lambert conformal conic projection on a sphere — the grid HRRR and most NCEP
/// mesoscale models are published on. Forward and inverse, in metres from the
/// projection origin.
/// </summary>
public sealed class LambertConformal
{
    /// <summary>Earth radius GRIB2 shape-of-earth code 6 specifies, metres.</summary>
    public const double GribSphereRadiusM = 6_371_229.0;

    private readonly double _n;
    private readonly double _f;
    private readonly double _rho0;
    private readonly double _lon0Rad;
    private readonly double _radius;

    public LambertConformal(
        double standardParallel1Deg, double standardParallel2Deg,
        double originLonDeg, double originLatDeg, double radiusM = GribSphereRadiusM)
    {
        _radius = radiusM;
        _lon0Rad = originLonDeg * Math.PI / 180.0;
        double phi1 = standardParallel1Deg * Math.PI / 180.0;
        double phi2 = standardParallel2Deg * Math.PI / 180.0;
        double phi0 = originLatDeg * Math.PI / 180.0;

        _n = Math.Abs(phi1 - phi2) < 1e-10
            ? Math.Sin(phi1)
            : Math.Log(Math.Cos(phi1) / Math.Cos(phi2)) /
              Math.Log(Tangent(phi2) / Tangent(phi1));
        _f = Math.Cos(phi1) * Math.Pow(Tangent(phi1), _n) / _n;
        _rho0 = _radius * _f / Math.Pow(Tangent(phi0), _n);
    }

    private static double Tangent(double latRad) => Math.Tan(Math.PI / 4.0 + latRad / 2.0);

    public (double X, double Y) Forward(double latDeg, double lonDeg)
    {
        double phi = latDeg * Math.PI / 180.0;
        double theta = _n * (NormalizeRad(lonDeg * Math.PI / 180.0 - _lon0Rad));
        double rho = _radius * _f / Math.Pow(Tangent(phi), _n);
        return (rho * Math.Sin(theta), _rho0 - rho * Math.Cos(theta));
    }

    public (double LatDeg, double LonDeg) Inverse(double x, double y)
    {
        double dy = _rho0 - y;
        double rho = Math.Sign(_n) * Math.Sqrt(x * x + dy * dy);
        double theta = Math.Atan2(Math.Sign(_n) * x, Math.Sign(_n) * dy);
        double lat = 2.0 * Math.Atan(Math.Pow(_radius * _f / rho, 1.0 / _n)) - Math.PI / 2.0;
        double lon = theta / _n + _lon0Rad;
        return (lat * 180.0 / Math.PI, NormalizeRad(lon) * 180.0 / Math.PI);
    }

    private static double NormalizeRad(double radians)
    {
        while (radians > Math.PI) radians -= 2 * Math.PI;
        while (radians < -Math.PI) radians += 2 * Math.PI;
        return radians;
    }
}
