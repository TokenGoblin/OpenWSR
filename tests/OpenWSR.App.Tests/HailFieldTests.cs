using System.IO;
using OpenWSR.Palettes;

namespace OpenWSR.App.Tests;

/// <summary>
/// The hail swath, on a committed MRMS MESH hour (2026-08-26 00:10Z, hourly maximum).
///
/// Counts are from <b>ecCodes 2.47.0</b> reading the same file. They matter because MRMS
/// publishes MESH under local discipline 209, so ecCodes reports its units as "unknown" and
/// nothing in the file states the scale — it has to be established from the values, and the
/// whole layer is wrong by a factor of 25 if that is guessed rather than checked.
/// </summary>
public sealed class HailFieldTests
{
    private static string MeshPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenWSR.slnx")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "assets", "testdata", "grib2",
            "MRMS_MESH_Max_60min_20260826-001000.grib2.gz");
    }

    private static (byte[] Levels, OpenWSR.Grib2.Grib2Grid Grid) Load(float minMm) =>
        MrmsController.Quantise(File.ReadAllBytes(MeshPath()), BuiltinTables.HailSize, minMm);

    private static int Drawn(byte[] levels) => levels.Count(l => l != 0);

    /// <summary>Turn a quantised level back into millimetres through the table it came from.</summary>
    private static float Millimetres(byte level)
    {
        var table = BuiltinTables.HailSize;
        return table.MinValue + level / 255f * table.Range;
    }

    [Fact]
    public void TheGridIsTheNationalMrmsGrid()
    {
        var (_, grid) = Load(12f);

        // ecCodes: Ni 7000, Nj 3500, increments 0.01 degrees.
        Assert.Equal(7000, grid.Nx);
        Assert.Equal(3500, grid.Ny);
        Assert.Equal(0.01, grid.DxDeg, 6);
    }

    [Fact]
    public void BothFlagValuesAreDropped()
    {
        // MESH carries no zeros at all: every cell is either a size or one of two flags —
        // -1 for "no hail here" inside coverage (16,122,011 cells) and -3 for "no radar
        // coverage" outside it (8,337,611). Drawing either paints the whole country.
        var (levels, _) = Load(12f);

        Assert.Equal(24_500_000, levels.Length);
        Assert.Equal(4295, Drawn(levels));   // ecCodes: values >= 12 mm
    }

    [Theory]
    // ecCodes counts at each named hail size, in millimetres.
    [InlineData(12f, 4295)]
    [InlineData(19f, 1535)]      // 0.75 in — the severe threshold
    [InlineData(25.4f, 614)]     // 1.00 in — a quarter
    [InlineData(44.45f, 64)]     // 1.75 in — a golf ball
    public void TheThresholdKeepsExactlyWhatTheReferenceSays(float minMm, int expected)
    {
        var (levels, _) = Load(minMm);
        Assert.Equal(expected, Drawn(levels));
    }

    [Fact]
    public void ValuesAreMillimetresNotInches()
    {
        // The peak of this hour is 67.6 mm, which is 2.66 in — a baseball, and a plausible
        // CONUS maximum. Read as inches it would be a five-foot hailstone.
        var (levels, _) = Load(12f);
        float peak = Millimetres(levels.Max());

        Assert.InRange(peak, 66f, 69f);
        Assert.InRange(peak / 25.4f, 2.5f, 2.8f);
    }

    [Fact]
    public void TheTableFadesInBelowSevereAndSaturatesAbove()
    {
        // A hail layer that paints pea hail solidly turns every thunderstorm into a swath,
        // and one that goes flat above golf-ball hides the worst of a storm.
        var table = BuiltinTables.HailSize;
        var rgba = table.BuildRgba256();

        byte AlphaAt(float mm)
        {
            int level = (int)Math.Clamp((mm - table.MinValue) / table.Range * 255f, 0, 255);
            return rgba[level * 4 + 3];
        }

        Assert.True(AlphaAt(5f) < AlphaAt(19f), "pea hail should be fainter than severe");
        Assert.True(AlphaAt(19f) < AlphaAt(45f), "severe should be fainter than golf ball");
        Assert.True(AlphaAt(70f) > 200, "baseball hail should be nearly opaque");
    }

    [Fact]
    public void TheHailLayerDrawsOverTheSweepAndTheCompositeUnderIt()
    {
        // They are different quantities, so unlike the mosaics they do not compete: hail is
        // read against the echo that produced it.
        Assert.Equal(OpenWSR.Render.MapView.OverlaySlot.Analysis, MrmsLayer.HailSize.Slot);
        Assert.Equal(OpenWSR.Render.MapView.OverlaySlot.Field, MrmsLayer.Composite.Slot);
        Assert.NotEqual(MrmsLayer.HailSize.Product, MrmsLayer.Composite.Product);
    }
}
