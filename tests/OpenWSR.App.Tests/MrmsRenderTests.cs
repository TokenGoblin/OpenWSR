using System.IO;
using OpenWSR.Geo;
using OpenWSR.Palettes;

namespace OpenWSR.App.Tests;

/// <summary>
/// The MRMS draw path, on the committed composite. The decode itself is already golden
/// against ecCodes in Grib2Tests; what is asserted here is everything after it — that the
/// quantisation drops what it should, and that the raster puts weather where the grid says
/// it is rather than merely somewhere.
/// </summary>
public class MrmsRenderTests
{
    private static string GribPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenWSR.slnx")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "assets", "testdata", "grib2",
            "MRMS_MergedReflectivityQCComposite_20260818-191441.grib2.gz");
    }

    private static (byte[] Levels, OpenWSR.Grib2.Grib2Grid Grid) Load() =>
        MrmsController.Quantise(File.ReadAllBytes(GribPath()));

    [Fact]
    public void QuantisingKeepsTheGridAndDropsTheNoCoverageFlag()
    {
        var (levels, grid) = Load();

        // ecCodes, via Grib2Tests: 7000x3500 at 0.01 deg, values -999 to 67.5 dBZ.
        Assert.Equal(7000, grid.Nx);
        Assert.Equal(3500, grid.Ny);
        Assert.Equal(24_500_000, levels.Length);

        int drawn = levels.Count(l => l != 0);
        // Most of the grid is no-coverage or clear air; a real composite paints a few
        // percent. Zero would mean the threshold ate everything, half would mean the
        // -999 flag was being coloured in.
        Assert.InRange(drawn / (double)levels.Length, 0.001, 0.25);
    }

    [Fact]
    public void TheNorthWestCornerIsNeverDrawn()
    {
        // The grid starts at 55N 130W, out in the Pacific off British Columbia, where the
        // network has no coverage at all. If the -999 flag leaked through, this is where
        // it would show first.
        var (levels, grid) = Load();
        Assert.Equal(0, levels[0]);
        Assert.Equal(0, levels[grid.Nx / 2]);
    }

    [Fact]
    public void RenderedRegionIsTransparentWhereTheGridIsEmpty()
    {
        var (levels, grid) = Load();
        var palette = BuiltinTables.Reflectivity.BuildRgba256();

        // A patch of the Pacific well west of the grid's own western edge.
        var (x0, y0) = GeoMath.ToMercator(40.0, -150.0);
        var (x1, y1) = GeoMath.ToMercator(45.0, -145.0);
        var bgra = MrmsController.RenderRegion(
            levels, grid, Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1),
            128, palette);

        for (int i = 3; i < bgra.Length; i += 4)
            Assert.Equal(0, bgra[i]);
    }

    [Fact]
    public void RenderedRegionPaintsWhereTheGridHasEcho()
    {
        var (levels, grid) = Load();
        var palette = BuiltinTables.Reflectivity.BuildRgba256();

        // Find the grid's strongest cell and ask for a small window around it.
        int best = 0;
        for (int i = 1; i < levels.Length; i++)
            if (levels[i] > levels[best]) best = i;
        Assert.True(levels[best] > 0, "the composite should contain some echo");

        int ix = best % grid.Nx, iy = best / grid.Nx;
        double lon = grid.Lon1Deg + ix * grid.DxDeg;
        double lat = grid.NorthToSouth
            ? grid.Lat1Deg - iy * grid.DyDeg
            : grid.Lat1Deg + iy * grid.DyDeg;

        var (cx, cy) = GeoMath.ToMercator(lat, lon);
        double half = 20_000; // 20 km of Mercator either way
        var bgra = MrmsController.RenderRegion(
            levels, grid, cx - half, cy - half, cx + half, cy + half, 128, palette);

        int painted = 0;
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) painted++;
        Assert.True(painted > 0, "the strongest cell in the grid should render as something");
    }

    [Fact]
    public void RenderIsGeoreferencedNotMerelyCentred()
    {
        // Two windows either side of a north-south line through the grid must not come out
        // identical — which they would if the projection back into grid space were ignoring
        // position.
        var (levels, grid) = Load();
        var palette = BuiltinTables.Reflectivity.BuildRgba256();

        byte[] Window(double lat, double lon)
        {
            var (x, y) = GeoMath.ToMercator(lat, lon);
            double half = 200_000;
            return MrmsController.RenderRegion(
                levels, grid, x - half, y - half, x + half, y + half, 64, palette);
        }

        var west = Window(39.0, -104.0);   // Colorado
        var east = Window(39.0, -84.0);    // Ohio
        Assert.False(west.SequenceEqual(east),
            "two different parts of the country rendered identically");
    }
}
