using OpenWSR.Grib2;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// GRIB2 golden tests. Expected values come from ecCodes 2.47 (ECMWF's reference
/// implementation) run over the same committed files.
/// </summary>
public class Grib2Tests
{
    [Fact]
    public void Mrms_PngPacked_LatLonGrid()
    {
        // MRMS composite reflectivity: 7000x3500 at 0.01 deg, PNG packing (template 5.41).
        var field = Grib2File.Decode(
            File.ReadAllBytes(TestData.Path("grib2/MRMS_MergedReflectivityQCComposite_20260818-191441.grib2.gz")));

        Assert.Equal(GridKind.LatLon, field.Grid.Kind);
        Assert.Equal(7000, field.Grid.Nx);
        Assert.Equal(3500, field.Grid.Ny);
        Assert.Equal(54.995, field.Grid.Lat1Deg, 4);
        Assert.Equal(-129.995, field.Grid.Lon1Deg, 4);
        Assert.Equal(0.01, field.Grid.DxDeg, 6);
        Assert.Equal(0.01, field.Grid.DyDeg, 6);
        Assert.Equal(24_500_000, field.Values.Length);

        // ecCodes: min -999 (the no-coverage flag), max 67.5 dBZ at index 5651266.
        Assert.Equal(-999f, field.Values[0], 3);
        Assert.Equal(67.5f, field.Values[5_651_266], 3);
        Assert.Equal(67.5f, field.Values.Max(), 3);
        Assert.Equal(-999f, field.Values.Min(), 3);
    }

    [Fact]
    public void Hrrr_ComplexPackedWithSpatialDifferencing_LambertGrid()
    {
        // HRRR composite reflectivity: Lambert grid (3.30), complex packing with
        // second-order spatial differencing (5.3) — the hard path.
        var field = Grib2File.Decode(
            File.ReadAllBytes(TestData.Path("grib2/HRRR_REFC_20260818_t18z_f01.grib2")));

        Assert.Equal(GridKind.LambertConformal, field.Grid.Kind);
        Assert.Equal(1799, field.Grid.Nx);
        Assert.Equal(1059, field.Grid.Ny);
        Assert.Equal(21.138123, field.Grid.Lat1Deg, 5);
        Assert.Equal(-122.719528, field.Grid.Lon1Deg, 5);
        Assert.Equal(-97.5, field.Grid.LovDeg, 4);
        Assert.Equal(38.5, field.Grid.Latin1Deg, 4);
        Assert.Equal(38.5, field.Grid.Latin2Deg, 4);
        Assert.Equal(3000, field.Grid.DxMetres, 1);
        Assert.Equal(1, field.ForecastHours);
        Assert.Equal(1_905_141, field.Values.Length);

        // ecCodes: min -10, max 66.25 at index 306823, and -10 at index 1000000.
        Assert.Equal(-10f, field.Values[0], 3);
        Assert.Equal(66.25f, field.Values[306_823], 3);
        Assert.Equal(-10f, field.Values[1_000_000], 3);
        Assert.Equal(66.25f, field.Values.Max(), 3);
        Assert.Equal(-10f, field.Values.Min(), 3);
    }
}
