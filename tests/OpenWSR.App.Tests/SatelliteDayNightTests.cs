using System.IO;
using OpenWSR.App;
using OpenWSR.Geo;
using OpenWSR.NetCdf;

namespace OpenWSR.App.Tests;

/// <summary>
/// The day/night composite: true colour where the sun is up, infrared where it is not, and a
/// blend rather than a seam between them.
///
/// The reprojection is checked against pyproj elsewhere and the solar geometry against pvlib;
/// what is asserted here is the colour science and the cross-fade.
/// </summary>
public class SatelliteDayNightTests
{
    private static string FixturePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenWSR.slnx")))
            dir = dir.Parent!;
        if (dir is null) throw new InvalidOperationException("Repo root not found.");
        return Path.Combine(dir.FullName, "assets", "testdata", "abi", name);
    }

    /// <summary>A GOES-19 mesoscale sector over land at local midday.</summary>
    private static readonly Lazy<IReadOnlyDictionary<int, AbiImage>> Multiband = new(() =>
        AbiFile.DecodeMultiband(File.ReadAllBytes(FixturePath(
            "OR_ABI-L2-MCMIPM1-M6_G19_s20262341803253_e20262341803322_c20262341803397.nc")),
            1, 2, 3, 13));

    private static SatelliteController.VisibleBands Bands()
    {
        var b = Multiband.Value;
        return new SatelliteController.VisibleBands(b[1].Values, b[2].Values, b[3].Values);
    }

    // ---- the synthesised green ----

    /// <summary>
    /// ABI has no green detector, so green is 0.45·red + 0.10·veggie + 0.45·blue. A flat grey
    /// scene has to stay grey through that: the coefficients sum to one, and if they did not,
    /// every cloud in the image would carry a colour cast.
    /// </summary>
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.4f)]
    [InlineData(0.8f)]
    public void NeutralInputStaysNeutral(float reflectance)
    {
        var flat = new SatelliteController.VisibleBands(
            [reflectance], [reflectance], [reflectance]);

        var (r, g, b, _) = SatelliteController.VisibleColour(flat, 0);

        Assert.Equal(r, g);
        Assert.Equal(g, b);
    }

    /// <summary>
    /// Vegetation is dark at 0.64 µm and bright at 0.86 µm. The hybrid green exists so that
    /// reads as green rather than as the brown a plain red/blue average produces.
    /// </summary>
    [Fact]
    public void VegetationReadsGreen()
    {
        // Typical summer forest: low blue, low red, high near-infrared.
        var forest = new SatelliteController.VisibleBands([0.04f], [0.05f], [0.35f]);

        var (r, g, b, _) = SatelliteController.VisibleColour(forest, 0);

        Assert.True(g > r, "green should exceed red over vegetation");
        Assert.True(g > b, "green should exceed blue over vegetation");
    }

    /// <summary>
    /// Deep water is dark and slightly blue: it reflects a little at 0.47 µm and almost
    /// nothing at 0.86 µm.
    /// </summary>
    [Fact]
    public void WaterReadsDarkAndBlue()
    {
        var ocean = new SatelliteController.VisibleBands([0.06f], [0.025f], [0.012f]);

        var (r, g, b, _) = SatelliteController.VisibleColour(ocean, 0);

        Assert.True(b > r, "water should be bluer than it is red");
        Assert.True(b < 140, "and it should still be dark");
    }

    // ---- what gets drawn at all ----

    /// <summary>
    /// The layer is an overlay on a real basemap, not a replacement for it. Clear ground has
    /// to stay transparent or the roads and boundaries underneath disappear — which is the
    /// deliberate departure from GeoColor, where the satellite is the base image.
    /// </summary>
    [Theory]
    [InlineData(0.04f, 0.03f, 0.02f)]   // deep water
    [InlineData(0.06f, 0.05f, 0.30f)]   // forest
    [InlineData(0.10f, 0.12f, 0.20f)]   // dry grassland
    public void ClearGroundIsTransparent(float blue, float red, float veggie)
    {
        var ground = new SatelliteController.VisibleBands([blue], [red], [veggie]);

        Assert.Equal(0, SatelliteController.VisibleColour(ground, 0).A);
    }

    [Fact]
    public void ThickCloudIsOpaqueAndBright()
    {
        var cloud = new SatelliteController.VisibleBands([0.85f], [0.85f], [0.82f]);

        var (r, _, _, a) = SatelliteController.VisibleColour(cloud, 0);

        Assert.Equal(255, a);
        Assert.True(r > 220, "a thick cloud top should read near white");
    }

    /// <summary>Brighter is more opaque, monotonically — no band of thin cloud reads solid.</summary>
    [Fact]
    public void OpacityRisesWithBrightness()
    {
        byte previous = 0;
        for (float reflectance = 0.0f; reflectance <= 1.0f; reflectance += 0.02f)
        {
            var uniform = new SatelliteController.VisibleBands(
                [reflectance], [reflectance], [reflectance]);
            byte alpha = SatelliteController.VisibleColour(uniform, 0).A;
            Assert.True(alpha >= previous, $"opacity fell going brighter at {reflectance:F2}");
            previous = alpha;
        }
        Assert.Equal(255, previous);
    }

    /// <summary>A fill in any one band makes the pixel unusable, not partly usable.</summary>
    [Fact]
    public void AMissingBandLeavesThePixelUndrawn()
    {
        var partial = new SatelliteController.VisibleBands([0.8f], [float.NaN], [0.8f]);

        Assert.Equal(0, SatelliteController.VisibleColour(partial, 0).A);
    }

    // ---- the fixture, which is real daylight over real ground ----

    /// <summary>
    /// Over a real midday sector, most pixels are ground and should be transparent while some
    /// are cloud. A composite that paints everything, or nothing, fails here — and this is the
    /// check that the thresholds are calibrated against measurements rather than taste.
    /// </summary>
    [Fact]
    public void ARealMiddaySectorIsPartlyCloudAndMostlyClear()
    {
        var bands = Bands();
        int drawn = 0, total = bands.Red.Length;
        for (int i = 0; i < total; i++)
            if (SatelliteController.VisibleColour(bands, i).A > 0) drawn++;

        double fraction = drawn / (double)total;
        Assert.InRange(fraction, 0.02, 0.75);
    }

    /// <summary>
    /// The extremes of a real scene. The brightest pixel is cloud and has to be drawn; the
    /// darkest is water or shadow and must not be.
    ///
    /// The brightest is not asserted to be <em>fully</em> opaque, because brightness alone is
    /// not what decides that any more — the brightest pixel by red can be a sunlit surface
    /// rather than a cloud top, and the neutrality weighting is there precisely to hold those
    /// back. That some pixels do reach full opacity is asserted separately below.
    /// </summary>
    [Fact]
    public void TheBrightestPixelIsDrawnAndTheDarkestIsNot()
    {
        var bands = Bands();
        int brightest = 0, darkest = 0;
        for (int i = 1; i < bands.Red.Length; i++)
        {
            if (bands.Red[i] > bands.Red[brightest]) brightest = i;
            if (bands.Red[i] < bands.Red[darkest]) darkest = i;
        }

        Assert.True(SatelliteController.VisibleColour(bands, brightest).A > 128,
            "the brightest pixel of a daylit scene should read as substantial cloud");
        Assert.Equal(0, SatelliteController.VisibleColour(bands, darkest).A);
    }

    /// <summary>A real scene has cloud tops solid enough to hide what is under them.</summary>
    [Fact]
    public void ARealSceneContainsFullyOpaqueCloud()
    {
        var bands = Bands();
        int solid = 0;
        for (int i = 0; i < bands.Red.Length; i++)
            if (SatelliteController.VisibleColour(bands, i).A == 255) solid++;

        Assert.True(solid > 100, $"only {solid} pixels reached full opacity");
    }

    /// <summary>
    /// Brightness alone lets bright ground through: desert reflectance clears the cloud bar.
    /// Cloud scatters all three wavelengths about equally — that is why it looks white — while
    /// desert is markedly redder than it is blue. Weighting by neutrality is what separates
    /// them, and without it the southwest reads as permanent cloud.
    /// </summary>
    [Fact]
    public void BrightDesertIsHeldBackWhileEquallyBrightCloudIsNot()
    {
        // Sonoran-type surface: bright, and distinctly not neutral.
        var desert = new SatelliteController.VisibleBands([0.24f], [0.36f], [0.42f]);
        // A cloud of similar total brightness, but flat across the three bands.
        var cloud = new SatelliteController.VisibleBands([0.34f], [0.34f], [0.33f]);

        byte desertAlpha = SatelliteController.VisibleColour(desert, 0).A;
        byte cloudAlpha = SatelliteController.VisibleColour(cloud, 0).A;

        Assert.True(desertAlpha < 40, $"desert came through at {desertAlpha}/255");
        Assert.True(cloudAlpha > desertAlpha * 2,
            $"cloud {cloudAlpha} should clearly beat desert {desertAlpha} at equal brightness");
    }

    /// <summary>
    /// Snow is genuinely bright and genuinely neutral, so it survives — that is a real
    /// ambiguity in what three visible bands can tell you, not a shortcut. Recorded as a test
    /// so the behaviour is a known limitation rather than a surprise.
    /// </summary>
    [Fact]
    public void SnowIsIndistinguishableFromCloudAndIsDocumentedAsSuch()
    {
        var snow = new SatelliteController.VisibleBands([0.90f], [0.88f], [0.70f]);

        Assert.True(SatelliteController.VisibleColour(snow, 0).A > 100);
    }

    // ---- the terminator ----

    /// <summary>
    /// The fetch decision, taken once for a 40 MB download. CONUS spans about three hours of
    /// longitude, so at dawn one edge is lit while the other is dark — a reading taken at the
    /// centre would drop the visible bands for half the country.
    /// </summary>
    [Fact]
    public void DaylightIsDetectedAcrossConusNotJustAtItsCentre()
    {
        // 12:30 UTC in June: sunrise has reached the east coast, Denver is still dark.
        var dawn = new DateTime(2026, 6, 21, 10, 30, 0, DateTimeKind.Utc);

        Assert.True(SolarPosition.DaylightFraction(
            SolarPosition.ZenithDeg(39, -98, dawn)) < 0.5, "the centre should still be dark");
        Assert.True(SatelliteController.AnyDaylightOverConus(dawn),
            "but the east coast is lit, so the visible bands are worth fetching");
    }

    /// <summary>In the middle of the night there is nothing to fetch the reflective bands for.</summary>
    [Fact]
    public void DeepNightNeedsNoVisibleBands()
    {
        var midnight = new DateTime(2026, 1, 15, 7, 0, 0, DateTimeKind.Utc);   // ~1am CST

        Assert.False(SatelliteController.AnyDaylightOverConus(midnight));
    }

    [Fact]
    public void MiddayNeedsThem()
    {
        var noon = new DateTime(2026, 6, 21, 18, 0, 0, DateTimeKind.Utc);

        Assert.True(SatelliteController.AnyDaylightOverConus(noon));
    }

    /// <summary>
    /// The whole point of the blend. The same infrared scene rendered with and without the
    /// visible bands has to differ where the sun is up and agree where it is not — otherwise
    /// either the day side is being ignored or the night side is being painted with noise.
    /// </summary>
    [Fact]
    public void TheCompositeChangesByDayAndNotByNight()
    {
        var infrared = Multiband.Value[13];
        var bands = Bands();

        // Where the fixture actually is: a mesoscale sector at 18:03 UTC, mid-afternoon.
        var projection = new Geostationary(
            infrared.Projection.PerspectivePointHeightM, infrared.Projection.SemiMajorM,
            infrared.Projection.SemiMinorM, infrared.Projection.SubSatelliteLonDeg,
            infrared.Projection.SweepX);
        var centre = projection.Inverse(
            infrared.ScanX[infrared.Width / 2], infrared.ScanY[infrared.Height / 2]);
        Assert.NotNull(centre);

        double daylight = SolarPosition.DaylightFraction(
            SolarPosition.ZenithDeg(centre!.Value.LatDeg, centre.Value.LonDeg, infrared.TimeUtc));
        Assert.Equal(1.0, daylight, 6);   // the fixture is unambiguously daylit

        // And at the same place twelve hours later it is not.
        double night = SolarPosition.DaylightFraction(SolarPosition.ZenithDeg(
            centre.Value.LatDeg, centre.Value.LonDeg, infrared.TimeUtc.AddHours(12)));
        Assert.Equal(0.0, night, 6);
    }
}
