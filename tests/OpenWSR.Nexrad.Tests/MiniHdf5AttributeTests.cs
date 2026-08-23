using OpenWSR.NetCdf;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Golden tests for HDF5 attribute reading, against the committed GLM and ABI files.
///
/// Reference values come from <b>h5py 3.16.0</b> reading the same two files. Attributes are
/// what make a NetCDF-4 variable mean anything — an array of scaled integers is not a
/// temperature until its <c>scale_factor</c> and <c>add_offset</c> are applied, and a
/// projected grid is unplaceable without its projection constants — so these are the values
/// a wrong answer would corrupt silently rather than loudly.
/// </summary>
public class MiniHdf5AttributeTests
{
    private static MiniHdf5 Glm() => MiniHdf5.Open(File.ReadAllBytes(TestData.Path(
        "glm/OR_GLM-L2-LCFA_G19_s20262301200000_e20262301200200_c20262301200221.nc")));

    private static MiniHdf5 Abi() => MiniHdf5.Open(File.ReadAllBytes(TestData.Path(
        "abi/OR_ABI-L2-CMIPC-M6C13_G19_s20262350211175_e20262350213560_c20262350214052.nc")));

    /// <summary>
    /// The scale and offset the GLM decoder used to carry as literals, because this reader
    /// could not read them. h5py prints exactly these; they now come from the file.
    /// </summary>
    [Fact]
    public void ReadsTheScaleAndOffsetGlmUsedToHardcode()
    {
        var attributes = Glm().AttributesOf("flash_energy");

        // A tolerance rather than a decimal-place count: these are stored as float32, so the
        // right comparison is relative to their own magnitude, and xUnit's place-count
        // overload only reaches 15 places anyway.
        Assert.Equal(9.99996e-16, attributes["scale_factor"].AsDouble(), 1e-21);
        Assert.Equal(2.8515e-16, attributes["add_offset"].AsDouble(), 1e-21);
        Assert.Equal("J", attributes["units"].AsString());
    }

    /// <summary>
    /// ABI stores brightness temperature as a scaled 12-bit integer. h5py:
    /// scale_factor=0.06145332, add_offset=89.62, valid_range=[0, 4095].
    /// </summary>
    [Fact]
    public void ReadsTheAbiBrightnessTemperatureScaling()
    {
        var attributes = Abi().AttributesOf("CMI");

        Assert.Equal(0.06145332, attributes["scale_factor"].AsDouble(), 8);
        Assert.Equal(89.62, attributes["add_offset"].AsDouble(), 4);
        Assert.Equal(0, attributes["valid_range"].AsDouble(0));
        Assert.Equal(4095, attributes["valid_range"].AsDouble(1));
        Assert.Equal(12, attributes["sensor_band_bit_depth"].AsDouble());
        Assert.Equal("K", attributes["units"].AsString());
    }

    /// <summary>
    /// The whole geostationary projection, which is nine attributes on a variable that holds
    /// no data at all. Every literal here is from h5py; getting any of them wrong puts the
    /// imagery somewhere else on the earth.
    /// </summary>
    [Fact]
    public void ReadsTheGeostationaryProjectionConstants()
    {
        var projection = Abi().AttributesOf("goes_imager_projection");

        Assert.Equal("geostationary", projection["grid_mapping_name"].AsString());
        Assert.Equal(35786023.0, projection["perspective_point_height"].AsDouble(), 3);
        Assert.Equal(6378137.0, projection["semi_major_axis"].AsDouble(), 3);
        Assert.Equal(6356752.31414, projection["semi_minor_axis"].AsDouble(), 5);
        Assert.Equal(298.2572221, projection["inverse_flattening"].AsDouble(), 7);
        Assert.Equal(0.0, projection["latitude_of_projection_origin"].AsDouble(), 10);
        Assert.Equal(-75.0, projection["longitude_of_projection_origin"].AsDouble(), 10);

        // The sweep axis decides which of the two scan angles is applied first, and getting
        // it backwards mirrors the image about the sub-satellite point. GOES-R is always 'x';
        // Meteosat is 'y'. It is worth reading rather than assuming.
        Assert.Equal("x", projection["sweep_angle_axis"].AsString());
    }

    /// <summary>
    /// The fixed-grid scan angles. h5py: x scale_factor=5.6e-05 rad, add_offset=-0.101332 —
    /// the published constants for the 2 km CONUS sector.
    /// </summary>
    [Fact]
    public void ReadsTheFixedGridScanAngles()
    {
        var file = Abi();

        var x = file.AttributesOf("x");
        Assert.Equal(5.6e-05, x["scale_factor"].AsDouble(), 10);
        Assert.Equal(-0.101332, x["add_offset"].AsDouble(), 8);
        Assert.Equal("rad", x["units"].AsString());

        var y = file.AttributesOf("y");
        Assert.Equal(-5.6e-05, y["scale_factor"].AsDouble(), 10);
        Assert.Equal(0.128212, y["add_offset"].AsDouble(), 8);
    }

    /// <summary>
    /// Text attributes are stored with a <em>scalar</em> dataspace — four bytes, no dimension
    /// list — where numeric ones are rank-1 arrays. A reader that assumes a dataspace message
    /// is at least eight bytes long drops every string in the file while still returning all
    /// the numbers, which looks like it works.
    /// </summary>
    [Fact]
    public void ReadsScalarTextAttributesAlongsideNumericArrays()
    {
        var attributes = Abi().AttributesOf("CMI");

        Assert.Equal("toa_brightness_temperature", attributes["standard_name"].AsString());
        Assert.Equal("goes_imager_projection", attributes["grid_mapping"].AsString());
        Assert.Equal(2, attributes["valid_range"].Count);   // still an array
        Assert.Equal(1, attributes["standard_name"].Count); // and this one a scalar
    }

    /// <summary>
    /// CMI is declared as signed 16-bit and flagged <c>_Unsigned = "true"</c>, which is how
    /// NetCDF-4 spells an unsigned type HDF5's netCDF-3 compatibility layer cannot express.
    /// The fill value reads as -1 and means 65535. Anything applying the scale without
    /// honouring this turns the fill into a temperature near absolute zero.
    /// </summary>
    [Fact]
    public void CarriesTheUnsignedFlagThatContradictsTheDeclaredType()
    {
        var file = Abi();

        Assert.Equal(Hdf5Kind.SignedInteger, file.Datasets["CMI"].Kind);
        Assert.Equal("true", file.AttributesOf("CMI")["_Unsigned"].AsString());
        Assert.Equal(-1, file.AttributesOf("CMI")["_FillValue"].AsDouble());
    }

    /// <summary>
    /// Past a handful of attributes HDF5 moves them out of the object header into a fractal
    /// heap. Every netCDF-4 variable crosses that threshold, so the dense path is the only
    /// one that ever fires in practice — a reader that handles just the compact form finds
    /// nothing at all and reports it as "no attributes" rather than as unsupported.
    /// </summary>
    [Fact]
    public void FindsDenselyStoredAttributes()
    {
        // h5py counts 16 on CMI, one of which is a DIMENSION_LIST of object references —
        // a datatype class this reader does not implement and deliberately skips.
        Assert.Equal(15, Abi().AttributesOf("CMI").Count);
        Assert.Equal(9, Abi().AttributesOf("goes_imager_projection").Count);
    }

    /// <summary>Root-group attributes are reachable the same way, with no dataset named.</summary>
    [Fact]
    public void ReadsRootGroupAttributes()
    {
        var root = Glm().AttributesOf();

        Assert.Equal("GLM L2 Lightning Detections: Events, Groups, and Flashes",
            root["title"].AsString());
        Assert.Equal("8km at nadir", root["spatial_resolution"].AsString());
    }

    [Fact]
    public void AskingForAMissingDatasetSaysSo()
    {
        var error = Assert.Throws<Hdf5FormatException>(() => Abi().AttributesOf("not_a_variable"));
        Assert.Contains("not_a_variable", error.Message);
    }

    [Fact]
    public void ReadingTextAsANumberSaysSo()
    {
        var units = Abi().AttributesOf("CMI")["units"];
        var error = Assert.Throws<Hdf5FormatException>(() => units.AsDouble());
        Assert.Contains("units", error.Message);
    }
}
