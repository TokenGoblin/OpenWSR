using OpenWSR.Nexrad;
using OpenWSR.Nexrad.Level3;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>
/// Assembles a TDWR volume out of its Level III products.
///
/// A WSR-88D volume arrives as one Level II file and decodes whole. A TDWR has to be built:
/// six separate products — three reflectivity tilts and three velocity tilts — fetched
/// independently and put together. They are not simultaneous either, since each is published
/// as its own scan completes, so the volume carries the newest of them as its time and the
/// individual sweeps keep their own.
/// </summary>
public sealed class TdwrFeed(Level3Client client)
{
    private static readonly ILogger Log = Serilog.Log.ForContext<TdwrFeed>();

    /// <summary>
    /// Fetch and assemble the newest volume for a TDWR site, or null when nothing is
    /// published — which is normal: these radars run a hazardous-weather scan strategy and
    /// go quiet in clear air, unlike the WSR-88D's continuous clear-air VCPs.
    /// </summary>
    public async Task<RadarVolume?> GetLatestAsync(RadarSite site, CancellationToken ct = default)
    {
        if (!site.IsTdwr)
            throw new ArgumentException($"{site.Icao} is not a TDWR.", nameof(site));

        var wanted = new List<(string Product, Moment Moment)>();
        foreach (var product in TdwrRadar.ReflectivityProducts)
            wanted.Add((product, Moment.Reflectivity));
        foreach (var product in TdwrRadar.VelocityProducts)
            wanted.Add((product, Moment.Velocity));

        // Six small products, so they go together rather than one after another.
        var fetched = await Task.WhenAll(wanted.Select(async w =>
        {
            try
            {
                var product = await client.GetLatestRadialImageAsync(site.Icao, w.Product, ct);
                return product is null ? null : new { Image = product, w.Moment, w.Product };
            }
            catch (Exception ex)
            {
                // One missing tilt should not lose the other five.
                Log.Warning(ex, "TDWR {Site} {Product} failed", site.Icao, w.Product);
                return null;
            }
        }));

        var present = fetched.Where(f => f is not null).ToList();
        if (present.Count == 0)
        {
            Log.Debug("TDWR {Site}: nothing published", site.Icao);
            return null;
        }

        // Ordered by elevation within each moment, which is what the tilt selector steps
        // through. The product suffix already encodes the tilt, but the angle is what the
        // beam-height maths and the label need.
        var sweeps = present
            .Select(f => TdwrRadar.ToSweep(f!.Image, f.Moment, site.ElevationM))
            .OrderBy(s => s.Moment)
            .ThenBy(s => s.ElevationAngleDeg)
            .ToList();

        // ElevationIndex is what the display uses to pair a reflectivity cut with the
        // velocity cut at the same tilt, so it has to be the tilt's position rather than the
        // sweep's position in this list.
        var byMoment = sweeps.GroupBy(s => s.Moment);
        var indexed = new List<Sweep>(sweeps.Count);
        foreach (var group in byMoment)
        {
            int index = 0;
            foreach (var sweep in group.OrderBy(s => s.ElevationAngleDeg))
                indexed.Add(sweep with { ElevationIndex = index++ });
        }

        var newest = indexed.Max(s => s.ScanTimeUtc);
        Log.Debug("TDWR {Site}: {Count} sweeps, newest {Time:HH:mm:ss}Z",
            site.Icao, indexed.Count, newest);

        return new RadarVolume(
            SiteId: site.Icao,
            StartTimeUtc: newest,
            LatDeg: indexed[0].RadarLatDeg,
            LonDeg: indexed[0].RadarLonDeg,
            AltitudeM: site.ElevationM,
            // TDWRs do not run a WSR-88D volume coverage pattern, and reporting one would be
            // a claim about a scan strategy this has no knowledge of.
            VcpNumber: 0,
            Vcp: null,
            Status: null,
            Sweeps: indexed);
    }
}
