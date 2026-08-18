using System.Globalization;
using System.Reflection;

namespace OpenWSR.Ingest;

public sealed record RadarSite(
    string Icao, string Name, string State, double LatDeg, double LonDeg, double ElevationM, bool IsTdwr)
{
    public override string ToString() => $"{Icao} — {Name}, {State}";
}

/// <summary>The WSR-88D/TDWR site table (NCEI HOMR), embedded at build time.</summary>
public static class RadarSites
{
    private static readonly Lazy<IReadOnlyList<RadarSite>> Cache = new(Load);

    public static IReadOnlyList<RadarSite> All => Cache.Value;

    public static RadarSite? ByIcao(string icao) =>
        All.FirstOrDefault(s => s.Icao.Equals(icao, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nearest WSR-88D to a point (TDWRs excluded — their Level II is not in the bucket).</summary>
    public static RadarSite Nearest(double latDeg, double lonDeg) =>
        All.Where(s => !s.IsTdwr)
            .MinBy(s => OpenWSR.Geo.GeoMath.DistanceM(latDeg, lonDeg, s.LatDeg, s.LonDeg))!;

    private static IReadOnlyList<RadarSite> Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("OpenWSR.Ingest.Resources.nexrad-sites.csv")
            ?? throw new InvalidOperationException("Embedded site table missing.");
        using var reader = new StreamReader(stream);

        var sites = new List<RadarSite>(220);
        reader.ReadLine(); // header
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split(',');
            if (f.Length < 7) continue;
            sites.Add(new RadarSite(
                f[0], f[1], f[2],
                double.Parse(f[3], CultureInfo.InvariantCulture),
                double.Parse(f[4], CultureInfo.InvariantCulture),
                double.Parse(f[5], CultureInfo.InvariantCulture),
                f[6] == "1"));
        }
        return sites;
    }
}
