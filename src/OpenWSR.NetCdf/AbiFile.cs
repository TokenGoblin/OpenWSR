namespace OpenWSR.NetCdf;

/// <summary>
/// The geostationary projection an ABI file describes, as plain numbers.
///
/// Deliberately not a projection <em>object</em>: the maths lives in <c>OpenWSR.Geo</c> and
/// this assembly does not reference it. <c>Placefiles</c> depending on <c>Geo</c> is the one
/// edge between the pure libraries and it stays the only one, so the decoder reports what
/// the file says and the caller builds the projection from it.
/// </summary>
public sealed record AbiProjection(
    double PerspectivePointHeightM,
    double SemiMajorM,
    double SemiMinorM,
    double SubSatelliteLonDeg,
    bool SweepX);

/// <summary>
/// One ABI Cloud and Moisture Imagery band on its fixed grid.
///
/// <paramref name="Values"/> is row-major, row 0 at the top — the order the file stores it —
/// and carries NaN wherever the pixel was fill or out of the valid range. Emissive bands are
/// brightness temperature in kelvin; reflective bands are a unitless reflectance factor,
/// which <paramref name="Units"/> distinguishes.
/// </summary>
public sealed record AbiImage(
    int Width,
    int Height,
    float[] Values,
    double[] ScanX,
    double[] ScanY,
    AbiProjection Projection,
    DateTime TimeUtc,
    int BandId,
    double BandWavelengthMicrons,
    string Units);

/// <summary>
/// Decoder for ABI L2 CMIP files — one band of cloud and moisture imagery, as published on
/// the NOAA GOES buckets every five minutes for CONUS.
///
/// Almost everything needed to interpret the pixels is an attribute rather than data: the
/// scale and offset that turn the stored integer into a temperature, the valid range that
/// separates a reading from a fill, the flag saying the declared signed type is a lie, and
/// the whole projection. See <see cref="MiniHdf5.AttributesOf"/>.
/// </summary>
public static class AbiFile
{
    /// <summary>GOES epoch: seconds since 2000-01-01 12:00:00 UTC, as GLM uses.</summary>
    private static readonly DateTime Epoch = new(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public static AbiImage Decode(byte[] netCdf)
    {
        var file = MiniHdf5.Open(netCdf);

        if (!file.Datasets.TryGetValue("CMI", out var cmi))
            throw new Hdf5FormatException(
                "No CMI variable — this is not an ABI Cloud and Moisture Imagery product.");
        if (cmi.Dimensions.Length != 2)
            throw new Hdf5FormatException(
                $"CMI has rank {cmi.Dimensions.Length}; a single-band CMIP product is two-dimensional.");

        int height = (int)cmi.Dimensions[0];
        int width = (int)cmi.Dimensions[1];

        var attributes = file.AttributesOf("CMI");
        double scale = Number(attributes, "scale_factor", 1.0);
        double offset = Number(attributes, "add_offset", 0.0);

        // CMI is declared signed 16-bit and flagged _Unsigned = "true", which is how
        // NetCDF-4 spells a type its netCDF-3 compatibility layer cannot express. The fill
        // reads as -1 and means 65535; scaling it as signed yields about -3900 K.
        bool unsigned = Text(attributes, "_Unsigned") == "true";
        int validMin = 0, validMax = 65535;
        if (attributes.TryGetValue("valid_range", out var range) && range.Count >= 2)
        {
            validMin = (int)AsStored(range.AsDouble(0), unsigned);
            validMax = (int)AsStored(range.AsDouble(1), unsigned);
        }

        var raw = file.ReadInt16("CMI");
        var values = new float[(long)width * height];
        for (int i = 0; i < values.Length; i++)
        {
            int stored = unsigned ? (ushort)raw[i] : raw[i];
            values[i] = stored < validMin || stored > validMax
                ? float.NaN
                : (float)(stored * scale + offset);
        }

        return new AbiImage(
            width, height, values,
            ScanAngles(file, "x", width),
            ScanAngles(file, "y", height),
            ReadProjection(file),
            Epoch.AddSeconds(file.ReadDouble("t")[0]),
            file.Datasets.ContainsKey("band_id") ? file.ReadInt32("band_id")[0] : 0,
            file.Datasets.ContainsKey("band_wavelength") ? file.ReadSingle("band_wavelength")[0] : 0,
            Text(attributes, "units") ?? "");
    }

    /// <summary>
    /// The scan angle of every column or row, in radians.
    ///
    /// These are read rather than reconstructed from the offset and a step. A fixed grid is
    /// regular by definition, so the two agree — but the file is the authority on its own
    /// geometry, and the sectors do not all share a step.
    /// </summary>
    private static double[] ScanAngles(MiniHdf5 file, string name, int expected)
    {
        var attributes = file.AttributesOf(name);
        double scale = Number(attributes, "scale_factor", 1.0);
        double offset = Number(attributes, "add_offset", 0.0);
        bool unsigned = Text(attributes, "_Unsigned") == "true";

        var raw = file.ReadInt16(name);
        if (raw.Length != expected)
            throw new Hdf5FormatException(
                $"'{name}' has {raw.Length} entries but CMI expects {expected}.");

        var angles = new double[expected];
        for (int i = 0; i < expected; i++)
            angles[i] = (unsigned ? (ushort)raw[i] : raw[i]) * scale + offset;
        return angles;
    }

    private static AbiProjection ReadProjection(MiniHdf5 file)
    {
        if (!file.Datasets.ContainsKey("goes_imager_projection"))
            throw new Hdf5FormatException("No goes_imager_projection — the grid cannot be placed.");

        var p = file.AttributesOf("goes_imager_projection");
        string? kind = Text(p, "grid_mapping_name");
        if (kind is not null && kind != "geostationary")
            throw new Hdf5FormatException($"Grid mapping '{kind}' is not supported (only geostationary).");

        return new AbiProjection(
            Number(p, "perspective_point_height", 35786023.0),
            Number(p, "semi_major_axis", 6378137.0),
            Number(p, "semi_minor_axis", 6356752.31414),
            Number(p, "longitude_of_projection_origin", -75.0),
            // GOES-R sweeps x. Read it anyway: the same product family from another agency
            // sweeps y, and the difference is a mirrored image rather than an error.
            (Text(p, "sweep_angle_axis") ?? "x") == "x");
    }

    private static double Number(
        IReadOnlyDictionary<string, Hdf5Attribute> attributes, string name, double fallback) =>
        attributes.TryGetValue(name, out var value) && value.Kind != Hdf5Kind.String
            ? value.AsDouble()
            : fallback;

    private static string? Text(
        IReadOnlyDictionary<string, Hdf5Attribute> attributes, string name) =>
        attributes.TryGetValue(name, out var value) && value.Kind == Hdf5Kind.String
            ? value.AsString()
            : null;

    /// <summary>
    /// valid_range is written in the declared signed type, so an unsigned upper bound past
    /// 32767 arrives negative. Reinterpreting it is the same trick the pixels need.
    /// </summary>
    private static long AsStored(double declared, bool unsigned) =>
        unsigned && declared < 0 ? (ushort)(short)declared : (long)declared;
}
