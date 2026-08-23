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
///
/// The energy scaling is read from the file's attributes. That is worth stating because it
/// was not always true: attributes were the one part of HDF5 this reader skipped, so the
/// constants were copied out of the specification instead. Reading them became necessary
/// for ABI, whose projection lives entirely in attributes, and GLM got the benefit.
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

        // Energy is stored as a scaled short, and the scale comes from the file's own
        // attributes. It used to be a pair of literals copied from the L2 specification,
        // because this reader could not walk attributes; it can now. The published values
        // are kept as the fallback rather than deleted — an older file that omits them
        // should still decode rather than raise.
        var energyAttributes = file.Datasets.ContainsKey("flash_energy")
            ? file.AttributesOf("flash_energy")
            : new Dictionary<string, Hdf5Attribute>();
        float energyScale = energyAttributes.TryGetValue("scale_factor", out var scale)
            ? (float)scale.AsDouble()
            : 9.99996e-16f;
        float energyOffset = energyAttributes.TryGetValue("add_offset", out var offset)
            ? (float)offset.AsDouble()
            : 2.8515e-16f;

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
