namespace OpenWSR.NetCdf;

/// <summary>
/// One lightning flash: where it was and how bright, as the Geostationary Lightning
/// Mapper reports it. A flash is the whole discharge — the optical groups and events
/// underneath it are aggregated away, which is the level a map wants.
/// </summary>
public readonly record struct LightningFlash(
    double LatDeg,
    double LonDeg,
    float EnergyJoules,
    DateTime TimeUtc);

/// <summary>
/// A GLM L2 "LCFA" product: twenty seconds of flashes over one satellite's field of view.
///
/// Times come from the file's own <c>product_time</c> rather than the per-flash offsets.
/// Every flash in the file falls inside a twenty-second window, which is finer than any
/// display distinguishes, and the offsets carry a scale and origin that would have to be
/// right to gain nothing.
/// </summary>
public static class GlmFile
{
    /// <summary>GOES epoch: seconds since 2000-01-01 12:00:00 UTC (J2000 noon).</summary>
    private static readonly DateTime Epoch = new(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Decode the flashes from a GLM file. Flags are a bit mask of processing problems, so
    /// anything non-zero is a flash the producer is not confident in and is dropped.
    /// </summary>
    public static IReadOnlyList<LightningFlash> DecodeFlashes(byte[] netCdf)
    {
        var file = MiniHdf5.Open(netCdf);

        var latitudes = file.ReadSingle("flash_lat");
        var longitudes = file.ReadSingle("flash_lon");
        if (latitudes.Length != longitudes.Length)
            throw new Hdf5FormatException("flash_lat and flash_lon disagree on how many flashes there are.");

        var quality = file.Datasets.ContainsKey("flash_quality_flag")
            ? file.ReadInt16("flash_quality_flag")
            : [];
        var energy = file.Datasets.ContainsKey("flash_energy")
            ? file.ReadInt16("flash_energy")
            : [];

        var time = Epoch.AddSeconds(file.ReadDouble("product_time")[0]);

        // Energy is stored as a scaled short. The scale is a file attribute, and attributes
        // are the one part of the format this reader does not walk, so the published L2
        // constants are used — they are fixed for the product, not per-file.
        const float energyScale = 9.99996e-16f;
        const float energyOffset = 2.8515e-16f;

        var flashes = new List<LightningFlash>(latitudes.Length);
        for (int i = 0; i < latitudes.Length; i++)
        {
            if (i < quality.Length && quality[i] != 0) continue;
            if (float.IsNaN(latitudes[i]) || float.IsNaN(longitudes[i])) continue;
            if (Math.Abs(latitudes[i]) > 90 || Math.Abs(longitudes[i]) > 180) continue;

            float joules = i < energy.Length ? energy[i] * energyScale + energyOffset : 0f;
            flashes.Add(new LightningFlash(latitudes[i], longitudes[i], joules, time));
        }
        return flashes;
    }

    /// <summary>The instant the file covers, from <c>product_time</c>.</summary>
    public static DateTime ProductTimeUtc(byte[] netCdf) =>
        Epoch.AddSeconds(MiniHdf5.Open(netCdf).ReadDouble("product_time")[0]);
}
