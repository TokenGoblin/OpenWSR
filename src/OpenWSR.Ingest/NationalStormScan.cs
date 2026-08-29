using OpenWSR.Geo;
using OpenWSR.Nexrad.Level3;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>The heaviest precipitation found at one radar site.</summary>
public sealed record Hotspot(
    RadarSite Site,
    double MaxVilKgM2,
    double LatDeg, double LonDeg,
    double RangeKm,
    DateTime ProductTimeUtc)
{
    /// <summary>
    /// How far the cell is from the place the search started at, in km; null when the search
    /// had no origin. Measured to the cell rather than to its radar, because the cell is what
    /// the camera flies to — a site 200 km away can hold a storm that is nearly overhead.
    /// </summary>
    public double? DistanceKm { get; init; }
}

/// <summary>
/// Sweeps every WSR-88D's newest digital VIL product (DVL, product 134 — vertically
/// integrated liquid is the standard "how much water is up there" measure) and ranks what
/// it finds, either by weight (the heaviest in the country) or by distance from a place
/// (the nearest real storm). Sites without a fresh product drop out.
/// </summary>
public static class NationalStormScan
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(NationalStormScan));

    /// <summary>
    /// The floor for "there is genuinely weather here". Below about this a DVL return is
    /// drizzle, virga or biological scatter, and flying the camera to the nearest of those
    /// would answer the question with an insect. 3.5 kg/m² is the conventional break between
    /// light and moderate precipitation; it is a floor on existence, not a severity judgement,
    /// so a plain rain shower still qualifies.
    /// </summary>
    public const double SignificantVilKgM2 = 3.5;

    /// <summary>Every site with a fresh DVL product, and its peak cell.</summary>
    public static async Task<IReadOnlyList<Hotspot>> ScanAsync(
        Action<int, int>? progress = null,
        TimeSpan? maxAge = null,
        CancellationToken ct = default)
    {
        var freshness = maxAge ?? TimeSpan.FromMinutes(30);
        using var client = new Level3Client();
        var sites = RadarSites.All.Where(s => !s.IsTdwr).ToList();
        var hotspots = new List<Hotspot>();
        var gate = new SemaphoreSlim(12);
        int scanned = 0;

        await Task.WhenAll(sites.Select(async site =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var vil = await client.GetLatestRadialImageAsync(site.Icao, "DVL", ct);
                if (vil is null || vil.ProductCode != DigitalVil.ProductCode)
                    return;
                if (DateTime.UtcNow - vil.ProductTimeUtc > freshness)
                    return;

                // Peak level in the grid; levels are monotonic in VIL.
                byte maxLevel = 0;
                int maxIndex = -1;
                for (int i = 0; i < vil.Levels.Length; i++)
                {
                    if (vil.Levels[i] > maxLevel)
                    {
                        maxLevel = vil.Levels[i];
                        maxIndex = i;
                    }
                }
                if (maxIndex < 0 || DigitalVil.Value(maxLevel, vil.Thresholds) is not { } maxVil)
                    return;

                int radial = maxIndex / vil.GateCount;
                int gateIndex = maxIndex % vil.GateCount;
                double azimuthDeg = vil.StartAnglesDeg[radial] + vil.DeltaAnglesDeg[radial] / 2.0;
                double rangeM = (vil.FirstBinIndex + gateIndex + 0.5) * DigitalVil.GateSpacingM;
                var (lat, lon) = GeoMath.Offset(
                    vil.RadarLatDeg, vil.RadarLonDeg, azimuthDeg * Math.PI / 180.0, rangeM);

                lock (hotspots)
                    hotspots.Add(new Hotspot(site, maxVil, lat, lon, rangeM / 1000.0, vil.ProductTimeUtc));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Hotspot scan: {Site} skipped", site.Icao);
            }
            finally
            {
                gate.Release();
                progress?.Invoke(Interlocked.Increment(ref scanned), sites.Count);
            }
        }));

        Log.Information("Hotspot scan: {Active}/{Total} sites reporting", hotspots.Count, sites.Count);
        return hotspots;
    }

    /// <summary>The heaviest precipitation in the country right now.</summary>
    public static async Task<Hotspot?> FindHeaviestAsync(
        Action<int, int>? progress = null,
        TimeSpan? maxAge = null,
        CancellationToken ct = default)
    {
        var winner = Heaviest(await ScanAsync(progress, maxAge, ct));
        Log.Information("Hotspot scan: heaviest is {Site} VIL {Vil:F1}",
            winner?.Site.Icao, winner?.MaxVilKgM2);
        return winner;
    }

    /// <summary>The nearest real storm to a place.</summary>
    public static async Task<Hotspot?> FindNearestAsync(
        double originLatDeg, double originLonDeg,
        Action<int, int>? progress = null,
        TimeSpan? maxAge = null,
        CancellationToken ct = default)
    {
        var winner = SelectNearest(await ScanAsync(progress, maxAge, ct), originLatDeg, originLonDeg);
        Log.Information("Hotspot scan: nearest is {Site} VIL {Vil:F1} at {Km:F0} km",
            winner?.Site.Icao, winner?.MaxVilKgM2, winner?.DistanceKm);
        return winner;
    }

    public static Hotspot? Heaviest(IReadOnlyList<Hotspot> hotspots) =>
        hotspots.MaxBy(h => h.MaxVilKgM2);

    /// <summary>
    /// The nearest cell to an origin that is actually raining. Ranking is by distance alone
    /// among those clearing <paramref name="minVilKgM2"/> — a heavier storm three states away
    /// does not win, which is the whole point of asking for the nearest one.
    /// </summary>
    public static Hotspot? SelectNearest(
        IReadOnlyList<Hotspot> hotspots,
        double originLatDeg, double originLonDeg,
        double minVilKgM2 = SignificantVilKgM2)
    {
        var located = hotspots
            .Select(h => h with
            {
                DistanceKm = GeoMath.DistanceM(originLatDeg, originLonDeg, h.LatDeg, h.LonDeg) / 1000.0
            })
            .ToList();

        // Nothing anywhere clears the bar, so there is no "nearest storm" to go to. Fall back
        // to the heaviest rather than reporting nothing: on a quiet day that is still the best
        // answer available, and a button that does nothing reads as broken rather than calm.
        return located.Where(h => h.MaxVilKgM2 >= minVilKgM2).MinBy(h => h.DistanceKm!.Value)
            ?? Heaviest(located);
    }
}
