using OpenWSR.Geo;
using OpenWSR.Nexrad.Level3;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>The heaviest precipitation found nationwide.</summary>
public sealed record Hotspot(
    RadarSite Site,
    double MaxVilKgM2,
    double LatDeg, double LonDeg,
    double RangeKm,
    DateTime ProductTimeUtc);

/// <summary>
/// Finds the heaviest precipitation in the USA right now: sweeps every WSR-88D's
/// newest digital VIL product (DVL, product 134 — vertically integrated liquid is
/// the standard "how much water is up there" measure) and ranks sites by their peak
/// VIL. Sites without a fresh product drop out.
/// </summary>
public static class NationalStormScan
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(NationalStormScan));

    public static async Task<Hotspot?> FindHeaviestAsync(
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

        var winner = hotspots.OrderByDescending(h => h.MaxVilKgM2).FirstOrDefault();
        Log.Information("Hotspot scan: {Active}/{Total} sites reporting; winner {Site} VIL {Vil:F1}",
            hotspots.Count, sites.Count, winner?.Site.Icao, winner?.MaxVilKgM2);
        return winner;
    }
}
