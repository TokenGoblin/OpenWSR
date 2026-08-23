using System.IO;
using OpenWSR.App;
using OpenWSR.Geo;
using OpenWSR.NetCdf;

namespace OpenWSR.App.Tests;

/// <summary>
/// Resampling ABI's fixed grid into Mercator, and the infrared enhancement applied to it.
///
/// The reprojection itself is checked against pyproj in <c>GeostationaryTests</c>; what is
/// asserted here is that the raster lands on the right ground and that the colour ramp says
/// something honest about temperature.
/// </summary>
public class SatelliteRasterTests
{
    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenWSR.slnx")))
            dir = dir.Parent!;
        if (dir is null) throw new InvalidOperationException("Repo root not found.");
        return Path.Combine(dir.FullName, "assets", "testdata", "abi",
            "OR_ABI-L2-CMIPC-M6C13_G19_s20262350211175_e20262350213560_c20262350214052.nc");
    }

    private static readonly Lazy<AbiImage> Image =
        new(() => AbiFile.Decode(File.ReadAllBytes(FixturePath())));

    private static readonly Lazy<OpenWSR.Render.MapView.ImageOverlay> Raster =
        new(() => SatelliteController.Rasterize(Image.Value, 0.6f));

    /// <summary>
    /// The CONUS sector should cover the continental United States and not much else. These
    /// are the Mercator bounds of the raster converted back to degrees.
    /// </summary>
    [Fact]
    public void TheRasterCoversTheContinentalUnitedStates()
    {
        var raster = Raster.Value;
        var (southLat, westLon) = GeoMath.FromMercator(raster.MinX, raster.MinY);
        var (northLat, eastLon) = GeoMath.FromMercator(raster.MaxX, raster.MaxY);

        // Key West is 24.5°N, the Canadian border 49°N, and the sector overshoots both.
        Assert.InRange(southLat, 10.0, 25.0);
        Assert.InRange(northLat, 49.0, 65.0);
        Assert.InRange(westLon, -160.0, -120.0);
        Assert.InRange(eastLon, -60.0, -40.0);
    }

    /// <summary>
    /// The northwest corner of the CONUS rectangle is off the limb of the earth — it is the
    /// furthest of the four from the sub-satellite point, and the file stores fill there.
    /// A bounding box built from the corners is therefore built partly from a non-place, and
    /// lands the whole raster on the wrong ground without looking broken. Walking the edges
    /// is what makes the box real.
    /// </summary>
    [Fact]
    public void TheNorthwestCornerOfTheSectorIsNotOnTheEarth()
    {
        var image = Image.Value;
        var projection = Projection(image);

        Assert.Null(projection.Inverse(image.ScanX[0], image.ScanY[0]));

        // The other three are on the planet, which is what makes this a trap rather than an
        // obvious failure: three of four corners answer, so a corner-based box looks fine.
        Assert.NotNull(projection.Inverse(image.ScanX[^1], image.ScanY[0]));
        Assert.NotNull(projection.Inverse(image.ScanX[0], image.ScanY[^1]));
        Assert.NotNull(projection.Inverse(image.ScanX[^1], image.ScanY[^1]));
    }

    private static Geostationary Projection(AbiImage image) => new(
        image.Projection.PerspectivePointHeightM, image.Projection.SemiMajorM,
        image.Projection.SemiMinorM, image.Projection.SubSatelliteLonDeg,
        image.Projection.SweepX);

    /// <summary>
    /// A pixel of the raster over a known place has to come from the grid cell over that
    /// place. Picked over Utah, where the fixture has cloud.
    /// </summary>
    [Fact]
    public void APixelOfTheRasterCarriesTheTemperatureAtThatGround()
    {
        var image = Image.Value;
        var raster = Raster.Value;
        var projection = Projection(image);

        // Find the raster pixel over Utah, then ask what ground its *centre* is on — the
        // resampler samples pixel centres, so comparing against the corner lands on the
        // neighbouring source cell and disagrees by a plausible-looking amount.
        var (mx, my) = GeoMath.ToMercator(40.3, -111.9);
        int column = (int)((mx - raster.MinX) / (raster.MaxX - raster.MinX) * raster.Width);
        int row = (int)((raster.MaxY - my) / (raster.MaxY - raster.MinY) * raster.Height);
        Assert.InRange(column, 0, raster.Width - 1);
        Assert.InRange(row, 0, raster.Height - 1);

        double centreX = raster.MinX + (column + 0.5) / raster.Width * (raster.MaxX - raster.MinX);
        double centreY = raster.MaxY - (row + 0.5) / raster.Height * (raster.MaxY - raster.MinY);
        var (lat, lon) = GeoMath.FromMercator(centreX, centreY);

        // Roughly where we asked for: the raster is about 2 km per pixel here.
        Assert.Equal(40.3, lat, 1);
        Assert.Equal(-111.9, lon, 1);

        var scan = projection.Forward(lat, lon);
        Assert.NotNull(scan);

        double dx = (image.ScanX[^1] - image.ScanX[0]) / (image.Width - 1);
        double dy = (image.ScanY[^1] - image.ScanY[0]) / (image.Height - 1);
        int i = (int)Math.Round((scan!.Value.X - image.ScanX[0]) / dx);
        int j = (int)Math.Round((scan.Value.Y - image.ScanY[0]) / dy);
        float kelvin = image.Values[j * image.Width + i];

        // A real cloud-top temperature over Utah, not a fill.
        Assert.InRange(kelvin, 150f, 350f);

        var expected = SatelliteController.InfraredColour(kelvin);
        int target = (row * raster.Width + column) * 4;
        Assert.Equal(expected.B, raster.Bgra[target + 0]);
        Assert.Equal(expected.G, raster.Bgra[target + 1]);
        Assert.Equal(expected.R, raster.Bgra[target + 2]);
        Assert.Equal(expected.A, raster.Bgra[target + 3]);
    }

    /// <summary>Most of the raster should carry something; a mostly-empty one means a bad box.</summary>
    [Fact]
    public void TheRasterIsMostlyCovered()
    {
        var raster = Raster.Value;
        int painted = 0;
        for (int i = 3; i < raster.Bgra.Length; i += 4)
            if (raster.Bgra[i] != 0) painted++;

        double fraction = painted / (double)(raster.Width * raster.Height);
        Assert.InRange(fraction, 0.20, 1.0);
    }

    /// <summary>
    /// Warm ground is transparent. An infrared image has a reading at every pixel, so an
    /// enhancement that paints all of them buries the basemap under a grey sheet — what the
    /// layer is for is showing where the cloud is.
    /// </summary>
    [Theory]
    [InlineData(300f)]   // 27 °C — summer ground
    [InlineData(290f)]
    public void WarmGroundIsNotDrawn(float kelvin) =>
        Assert.Equal(0, SatelliteController.InfraredColour(kelvin).A);

    /// <summary>Colder is more opaque, all the way down: nothing warm hides something cold.</summary>
    [Fact]
    public void OpacityRisesMonotonicallyAsCloudTopsGetColder()
    {
        byte previous = 0;
        for (float kelvin = 283f; kelvin >= 190f; kelvin -= 1f)
        {
            byte alpha = SatelliteController.InfraredColour(kelvin).A;
            Assert.True(alpha >= previous,
                $"{kelvin:F0} K became more transparent than the warmer step above it");
            previous = alpha;
        }
    }

    /// <summary>
    /// Past about -40 °C the tops are glaciated and deep, and every broadcast infrared
    /// product colours them. Grey up to there, colour beyond.
    /// </summary>
    [Fact]
    public void DeepColdTopsAreColouredAndOrdinaryCloudIsGrey()
    {
        var ordinary = SatelliteController.InfraredColour(260f);
        Assert.Equal(ordinary.R, ordinary.G);
        Assert.Equal(ordinary.G, ordinary.B);

        var deep = SatelliteController.InfraredColour(200f);
        Assert.True(deep.R != deep.G || deep.G != deep.B,
            "an overshooting top should not read as another shade of grey");
        Assert.Equal(255, deep.A);
    }
}
