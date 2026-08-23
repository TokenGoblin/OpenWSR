using OpenWSR.NetCdf;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// The ABI Cloud and Moisture Imagery decoder, against a committed GOES-19 band 13 CONUS file.
///
/// Reference values come from <b>h5py 3.16.0</b> and NumPy applying the file's own attributes
/// — an independent implementation, per this project's standard. Where a literal is a
/// brightness temperature it was printed by NumPy, not by this code.
/// </summary>
public class AbiFileTests
{
    private static AbiImage Decoded() => AbiFile.Decode(File.ReadAllBytes(TestData.Path(
        "abi/OR_ABI-L2-CMIPC-M6C13_G19_s20262350211175_e20262350213560_c20262350214052.nc")));

    private static readonly Lazy<AbiImage> Cached = new(Decoded);

    private static float At(AbiImage image, int row, int column) =>
        image.Values[row * image.Width + column];

    [Fact]
    public void ReadsTheConusGridShapeAndBand()
    {
        var image = Cached.Value;

        Assert.Equal(2500, image.Width);      // h5py: CMI.shape == (1500, 2500)
        Assert.Equal(1500, image.Height);
        Assert.Equal(13, image.BandId);
        Assert.Equal(10.33, image.BandWavelengthMicrons, 2);
        Assert.Equal("K", image.Units);
    }

    /// <summary>
    /// Individual pixels, scaled by hand in NumPy from the raw stored integer.
    /// </summary>
    [Theory]
    [InlineData(750, 1250, 298.008211)]
    [InlineData(1499, 2499, 295.550078)]
    [InlineData(1200, 600, 269.555324)]
    public void PixelsMatchNumpy(int row, int column, double kelvin) =>
        Assert.Equal(kelvin, At(Cached.Value, row, column), 4);

    /// <summary>
    /// The corners of the CONUS rectangle are off the limb of the earth, so the instrument
    /// never measured them and the file stores 65535 there. Read as the declared signed type
    /// that is -1, and scaling it gives about -3900 K — a number that would sail through any
    /// range check written for temperatures and paint the corner of the map solid.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 200)]
    public void FillPixelsAreNotATemperature(int row, int column) =>
        Assert.True(float.IsNaN(At(Cached.Value, row, column)),
            "a pixel outside valid_range must decode as unsampled, never as a value");

    /// <summary>
    /// NumPy over the whole grid: 3,702,837 of 3,750,000 pixels valid — 98.74 per cent, the
    /// remainder being the off-limb corners.
    /// </summary>
    [Fact]
    public void TheOffLimbCornersAreTheOnlyGapsInTheGrid()
    {
        var image = Cached.Value;
        int valid = image.Values.Count(v => !float.IsNaN(v));

        Assert.Equal(3_702_837, valid);
        Assert.Equal(3_750_000, image.Values.Length);
    }

    /// <summary>
    /// The strongest check available, and it is the file's own opinion rather than ours: ABI
    /// stores <c>min</c>, <c>max</c> and <c>mean_brightness_temperature</c> as plain floats,
    /// computed by the producer before quantisation. Decoding the pixels and taking those
    /// three statistics has to reproduce them. A wrong scale, a wrong offset, a sign error on
    /// the unsigned reinterpretation or a mishandled fill all move these immediately.
    ///
    /// The tolerance is a quarter kelvin because the producer worked from the unquantised
    /// radiances; the mean, where quantisation averages out, agrees to five decimal places.
    /// </summary>
    [Fact]
    public void StatisticsReproduceTheFilesOwn()
    {
        var image = Cached.Value;
        var present = image.Values.Where(v => !float.IsNaN(v)).ToArray();

        Assert.Equal(188.473618, present.Min(), 0.05);
        Assert.Equal(307.699158, present.Max(), 0.05);
        Assert.Equal(277.801636, present.Average(v => (double)v), 0.001);
    }

    /// <summary>
    /// Physics rather than a reference value: this is a thermal infrared window band over
    /// North America, so every reading has to be a temperature something in the atmosphere
    /// could actually be. Cold cloud tops reach the tropopause near 190 K; the warmest is
    /// summer ground.
    /// </summary>
    [Fact]
    public void EveryReadingIsAPlausibleBrightnessTemperature()
    {
        var present = Cached.Value.Values.Where(v => !float.IsNaN(v)).ToArray();

        // Bounds rather than Assert.All: this is 3.7 million values, and when the decode is
        // wrong every one of them fails, which turns a red test into a several-minute one.
        Assert.InRange(present.Min(), 150f, 350f);
        Assert.InRange(present.Max(), 150f, 350f);
    }

    /// <summary>
    /// The scan angles, read from the file rather than reconstructed. h5py prints the first
    /// and last of each; x[0] is the grid's own add_offset.
    /// </summary>
    [Fact]
    public void ScanAnglesMatchTheFixedGrid()
    {
        var image = Cached.Value;

        Assert.Equal(2500, image.ScanX.Length);
        Assert.Equal(1500, image.ScanY.Length);
        Assert.Equal(-0.101332001, image.ScanX[0], 9);
        Assert.Equal(+0.038612001, image.ScanX[^1], 9);
        Assert.Equal(+0.128212005, image.ScanY[0], 9);
        Assert.Equal(+0.044268004, image.ScanY[^1], 9);

        // North is up and west is left: y decreases down the image, x increases to the right.
        Assert.True(image.ScanY[0] > image.ScanY[^1]);
        Assert.True(image.ScanX[0] < image.ScanX[^1]);
    }

    [Fact]
    public void ReadsTheProjectionTheGridIsOn()
    {
        var projection = Cached.Value.Projection;

        Assert.Equal(35786023.0, projection.PerspectivePointHeightM, 3);
        Assert.Equal(6378137.0, projection.SemiMajorM, 3);
        Assert.Equal(6356752.31414, projection.SemiMinorM, 5);
        Assert.Equal(-75.0, projection.SubSatelliteLonDeg, 6);
        Assert.True(projection.SweepX);
    }

    /// <summary>h5py: t = 840723156.795 seconds after the GOES epoch.</summary>
    [Fact]
    public void ReadsTheScanMidpointTime()
    {
        var time = Cached.Value.TimeUtc;

        Assert.Equal(DateTimeKind.Utc, time.Kind);
        Assert.Equal(new DateTime(2026, 8, 23, 2, 12, 36, DateTimeKind.Utc), time,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ANonImageryFileIsRejectedByName()
    {
        var glm = File.ReadAllBytes(TestData.Path(
            "glm/OR_GLM-L2-LCFA_G19_s20262301200000_e20262301200200_c20262301200221.nc"));

        var error = Assert.Throws<Hdf5FormatException>(() => AbiFile.Decode(glm));
        Assert.Contains("CMI", error.Message);
    }
}
