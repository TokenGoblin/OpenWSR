using OpenWSR.NetCdf;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// The multiband (MCMIP) decoder, against a committed GOES-19 mesoscale file captured at
/// local midday so the reflective bands carry real signal rather than zeros.
///
/// Reference values are from <b>h5py 3.16.0</b> and NumPy applying the file's own attributes.
/// A mesoscale sector rather than CONUS purely for size: the structure is identical and the
/// decoder cannot tell them apart, but it is 4.6 MB against 57 for all sixteen bands.
/// </summary>
public class AbiMultibandTests
{
    private static byte[] Bytes() => File.ReadAllBytes(TestData.Path(
        "abi/OR_ABI-L2-MCMIPM1-M6_G19_s20262341803253_e20262341803322_c20262341803397.nc"));

    private static readonly Lazy<IReadOnlyDictionary<int, AbiImage>> Bands =
        new(() => AbiFile.DecodeMultiband(Bytes(), 1, 2, 3, 13));

    private static float At(AbiImage image, int row, int column) =>
        image.Values[row * image.Width + column];

    [Fact]
    public void DecodesEveryRequestedBandOnOneGrid()
    {
        var bands = Bands.Value;

        Assert.Equal([1, 2, 3, 13], bands.Keys.OrderBy(k => k));
        foreach (var (number, image) in bands)
        {
            Assert.Equal(500, image.Width);      // h5py: CMI_C02.shape == (500, 500)
            Assert.Equal(500, image.Height);
            Assert.Equal(number, image.BandId);
        }
    }

    /// <summary>
    /// The point of the multiband product: every band is already on the same grid, so a
    /// composite of several needs no reconciliation. Asking for them separately returns three
    /// different resolutions — 1 km, 0.5 km and 1 km for these three — that would have to be
    /// co-registered first.
    /// </summary>
    [Fact]
    public void EveryBandSharesOneSetOfScanAngles()
    {
        var bands = Bands.Value;
        var reference = bands[13];

        foreach (var image in bands.Values)
        {
            Assert.Equal(reference.ScanX, image.ScanX);
            Assert.Equal(reference.ScanY, image.ScanY);
            Assert.Equal(reference.TimeUtc, image.TimeUtc);
            Assert.Equal(reference.Projection, image.Projection);
        }
    }

    /// <summary>
    /// Individual pixels, scaled by hand in NumPy. Two bands with different scalings — a
    /// reflectance factor and a temperature — so a single scale applied to everything would
    /// show up here.
    /// </summary>
    [Theory]
    [InlineData(1, 250, 250, 0.173016)]
    [InlineData(2, 250, 250, 0.126032)]
    [InlineData(3, 250, 250, 0.362857)]
    [InlineData(13, 250, 250, 292.723226)]
    [InlineData(2, 100, 400, 0.215555)]
    [InlineData(13, 100, 400, 285.717547)]
    public void PixelsMatchNumpy(int band, int row, int column, double expected) =>
        // Three places, because the values are float32: near 286 K that type carries about
        // 1e-4, so a tighter tolerance would be testing the storage rather than the decode.
        Assert.Equal(expected, At(Bands.Value[band], row, column), 3);

    /// <summary>
    /// Reflective bands are a unitless reflectance factor; emissive ones are kelvin. The units
    /// attribute is what tells them apart, and a composite that mixes them up would treat a
    /// 290 K temperature as a reflectance of 290.
    /// </summary>
    [Fact]
    public void ReflectiveAndEmissiveBandsCarryTheirOwnUnits()
    {
        var bands = Bands.Value;

        Assert.Equal("1", bands[1].Units);
        Assert.Equal("1", bands[2].Units);
        Assert.Equal("1", bands[3].Units);
        Assert.Equal("K", bands[13].Units);
    }

    /// <summary>
    /// Physics: reflectance is a fraction, brightness temperature is a temperature. Ranges
    /// from NumPy over the whole sector. Reflectance slightly above 1 is real and expected —
    /// it is a reflectance <em>factor</em>, not an albedo, and a bright cloud viewed near
    /// backscatter can exceed unity.
    /// </summary>
    [Theory]
    [InlineData(1, 0.09683, 1.06413)]
    [InlineData(2, 0.03365, 1.02063)]
    [InlineData(3, 0.01460, 1.21524)]
    public void ReflectanceStaysInItsPhysicalRange(int band, double min, double max)
    {
        var present = Bands.Value[band].Values.Where(v => !float.IsNaN(v)).ToArray();

        Assert.Equal(min, present.Min(), 0.001);
        Assert.Equal(max, present.Max(), 0.001);
    }

    [Fact]
    public void BrightnessTemperatureStaysInItsPhysicalRange()
    {
        var present = Bands.Value[13].Values.Where(v => !float.IsNaN(v)).ToArray();

        Assert.Equal(202.01813, present.Min(), 0.05);
        Assert.Equal(307.53348, present.Max(), 0.05);
        Assert.Equal(281.55648, present.Average(v => (double)v), 0.01);
    }

    /// <summary>
    /// A mesoscale sector sits well inside the disk, so unlike CONUS every one of its pixels
    /// was measured — 250,000 of 250,000 per NumPy. That makes it the fixture where a
    /// spurious fill would be obvious.
    /// </summary>
    [Fact]
    public void EveryPixelOfAMesoscaleSectorWasMeasured()
    {
        foreach (var image in Bands.Value.Values)
            Assert.Equal(250_000, image.Values.Count(v => !float.IsNaN(v)));
    }

    /// <summary>
    /// Vegetation is bright at 0.86 µm and dark at 0.64 µm — that contrast is the whole basis
    /// of the vegetation index, and it is why band 3 cannot stand in for band 2 as a "visible"
    /// channel: land would read as cloud. h5py means: C02 0.264, C03 0.431.
    /// </summary>
    [Fact]
    public void TheNearInfraredBandIsBrighterThanRedOverLand()
    {
        double red = Bands.Value[2].Values.Where(v => !float.IsNaN(v)).Average(v => (double)v);
        double veggie = Bands.Value[3].Values.Where(v => !float.IsNaN(v)).Average(v => (double)v);

        Assert.Equal(0.26399, red, 0.001);
        Assert.Equal(0.43095, veggie, 0.001);
        Assert.True(veggie > red);
    }

    [Fact]
    public void ReadsTheScanTimeAndProjection()
    {
        var image = Bands.Value[13];

        // h5py: t = 840693808.82 seconds after the GOES epoch.
        Assert.Equal(new DateTime(2026, 8, 22, 18, 3, 28, DateTimeKind.Utc), image.TimeUtc,
            TimeSpan.FromSeconds(1));
        Assert.Equal(-75.0, image.Projection.SubSatelliteLonDeg, 6);
        Assert.Equal(-0.028532000, image.ScanX[0], 8);
        Assert.Equal(+0.119811997, image.ScanY[0], 8);
    }

    /// <summary>A single-band file has no CMI_Cnn variables, and says so rather than guessing.</summary>
    [Fact]
    public void ASingleBandFileIsRejectedByName()
    {
        var single = File.ReadAllBytes(TestData.Path(
            "abi/OR_ABI-L2-CMIPC-M6C13_G19_s20262350211175_e20262350213560_c20262350214052.nc"));

        var error = Assert.Throws<Hdf5FormatException>(() => AbiFile.DecodeMultiband(single, 13));
        Assert.Contains("CMI_C13", error.Message);
    }

    /// <summary>And the single-band decoder still rejects a multiband one.</summary>
    [Fact]
    public void AMultibandFileIsRejectedBySingleBandDecode()
    {
        var error = Assert.Throws<Hdf5FormatException>(() => AbiFile.Decode(Bytes()));
        Assert.Contains("CMI", error.Message);
    }
}
