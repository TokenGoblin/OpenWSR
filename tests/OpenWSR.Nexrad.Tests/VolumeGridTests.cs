using OpenWSR.Geo;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// The Cartesian resampling behind the 3D view. There is no reference implementation to
/// check a volume grid against, so these assert <em>physics</em>: the beam geometry must
/// invert exactly, the places a radar cannot see must come back empty, and a cell must
/// hold the value of the beam that actually passed through it.
/// </summary>
public sealed class VolumeGridTests(DecodedVolumes volumes) : IClassFixture<DecodedVolumes>
{
    private static IReadOnlyList<Sweep> Reflectivity(RadarVolume volume) =>
        [.. volume.Sweeps.Where(s => s.Moment == Moment.Reflectivity)];

    // ---- beam geometry ----

    [Theory]
    [InlineData(0.5, 10_000)]
    [InlineData(0.5, 230_000)]
    [InlineData(4.3, 100_000)]
    [InlineData(19.5, 50_000)]
    [InlineData(0.5, 1_000)]
    public void TheBeamInverseUndoesTheBeamPath(double elevationDeg, double slantM)
    {
        // BeamPath says where a beam gets to; BeamAngleTo says which beam gets there. If
        // they disagree, every Cartesian product built on them is subtly in the wrong place.
        double elevationRad = elevationDeg * Math.PI / 180.0;
        var (groundM, heightM) = GeoMath.BeamPath(slantM, elevationRad);

        var (backElevation, backSlant) = GeoMath.BeamAngleTo(groundM, heightM);

        Assert.Equal(elevationRad, backElevation, 9);
        Assert.Equal(slantM, backSlant, 3);
    }

    [Fact]
    public void APointDirectlyOverheadNeedsAVerticalBeam()
    {
        // Ground range zero: the only beam that reaches it points straight up, which no
        // WSR-88D cut does. This is the cone of silence, stated as geometry.
        var (elevation, slant) = GeoMath.BeamAngleTo(0, 8_000);

        Assert.Equal(Math.PI / 2, elevation, 9);
        Assert.Equal(8_000, slant, 3);
    }

    [Fact]
    public void ADistantLowPointNeedsANegativeElevation()
    {
        // At 200 km the 0.5° beam is already 3 km up, so anything near the ground out there
        // is below the horizon and needs a beam pointed down. No cut is, which is why the
        // grid is empty under the lowest beam.
        var (elevation, _) = GeoMath.BeamAngleTo(200_000, 0);

        Assert.True(elevation < 0, $"expected a depression angle, got {elevation * 180 / Math.PI:F2}°");
    }

    // ---- the grid ----

    private VolumeGrid MooreGrid(int horizontal = 96, int vertical = 24) =>
        VolumeGrid3D.Build(Reflectivity(volumes.Moore), -30f, 80f,
            horizontalCells: horizontal, verticalCells: vertical);

    [Fact]
    public void TheGridHasTheShapeItWasAskedFor()
    {
        var grid = MooreGrid(64, 16);

        Assert.Equal(64, grid.Nx);
        Assert.Equal(64, grid.Ny);
        Assert.Equal(16, grid.Nz);
        Assert.Equal(64 * 64 * 16, grid.Voxels.Length);
    }

    [Fact]
    public void IndexingIsXFastestSoAnUploadCanBeOneBlockCopy()
    {
        var grid = MooreGrid(8, 4);

        Assert.Equal(0, grid.Index(0, 0, 0));
        Assert.Equal(1, grid.Index(1, 0, 0));
        Assert.Equal(8, grid.Index(0, 1, 0));
        Assert.Equal(64, grid.Index(0, 0, 1));
    }

    [Fact]
    public void TheMooreVolumeFillsAMeaningfulPartOfTheBox()
    {
        // A supercell day over central Oklahoma. If this came back nearly empty the
        // sampling is broken; if it came back nearly full the empty regions are not being
        // respected. Both failures are silent in a screenshot.
        var grid = MooreGrid();
        double filled = grid.FilledCount / (double)grid.Voxels.Length;

        Assert.InRange(filled, 0.02, 0.60);
    }

    [Fact]
    public void TheConeOfSilenceIsEmpty()
    {
        // Directly above the radar, above the height the highest cut reaches. No beam goes
        // there, so nothing may be written there — this is the hole every real 3D radar
        // view shows over the site.
        var grid = MooreGrid();
        int centreX = grid.Nx / 2, centreY = grid.Ny / 2;
        double layer = (grid.TopHeightM - grid.BaseHeightM) / grid.Nz;

        int checkedVoxels = 0;
        for (int z = 0; z < grid.Nz; z++)
        {
            double height = grid.BaseHeightM + (z + 0.5) * layer;
            // Above the top cut's reach at the horizontal distance of the centre cells.
            if (height < 6_000) continue;
            for (int y = centreY - 1; y <= centreY; y++)
                for (int x = centreX - 1; x <= centreX; x++)
                {
                    Assert.Equal(0, grid.Voxels[grid.Index(x, y, z)]);
                    checkedVoxels++;
                }
        }
        Assert.True(checkedVoxels > 0, "the test checked nothing");
    }

    [Fact]
    public void NothingIsWrittenAboveTheHighestCut()
    {
        // The top cut of VCP 12 is 19.5°. At the edge of the box that beam is far below the
        // top of the grid, so the upper corners must be untouched.
        var grid = MooreGrid();
        double topElevation = grid.ElevationsUsed[^1] * Math.PI / 180.0;

        int examined = 0;
        double cell = 2.0 * grid.HalfWidthM / grid.Nx;
        double layer = (grid.TopHeightM - grid.BaseHeightM) / grid.Nz;

        for (int z = 0; z < grid.Nz; z++)
        {
            double height = grid.BaseHeightM + (z + 0.5) * layer;
            for (int y = 0; y < grid.Ny; y += 7)
                for (int x = 0; x < grid.Nx; x += 7)
                {
                    double east = -grid.HalfWidthM + (x + 0.5) * cell;
                    double north = -grid.HalfWidthM + (y + 0.5) * cell;
                    double ground = Math.Sqrt(east * east + north * north);
                    if (ground > grid.HalfWidthM) continue;

                    var (elevation, _) = GeoMath.BeamAngleTo(ground, height);
                    // Well above the top cut, beyond the half-degree edge tolerance.
                    if (elevation < topElevation + 2 * Math.PI / 180.0) continue;

                    Assert.Equal(0, grid.Voxels[grid.Index(x, y, z)]);
                    examined++;
                }
        }
        Assert.True(examined > 100, $"only {examined} voxels were above the top cut");
    }

    [Fact]
    public void EveryFilledVoxelSitsInsideTheScannedElevations()
    {
        // The converse of the two tests above, over the whole grid: data may only appear
        // where a beam was, within the half-degree of slack the edge cuts are given.
        var grid = MooreGrid(64, 20);
        double lowest = grid.ElevationsUsed[0] * Math.PI / 180.0 - 0.6 * Math.PI / 180.0;
        double highest = grid.ElevationsUsed[^1] * Math.PI / 180.0 + 0.6 * Math.PI / 180.0;

        double cell = 2.0 * grid.HalfWidthM / grid.Nx;
        double layer = (grid.TopHeightM - grid.BaseHeightM) / grid.Nz;

        for (int z = 0; z < grid.Nz; z++)
        {
            double height = grid.BaseHeightM + (z + 0.5) * layer;
            for (int y = 0; y < grid.Ny; y++)
                for (int x = 0; x < grid.Nx; x++)
                {
                    if (grid.Voxels[grid.Index(x, y, z)] == 0) continue;

                    double east = -grid.HalfWidthM + (x + 0.5) * cell;
                    double north = -grid.HalfWidthM + (y + 0.5) * cell;
                    var (elevation, _) = GeoMath.BeamAngleTo(
                        Math.Sqrt(east * east + north * north), height);

                    Assert.InRange(elevation, lowest, highest);
                }
        }
    }

    [Fact]
    public void NothingIsWrittenOutsideTheRangeDisc()
    {
        // The box is square and the radar's reach is round, so the corners are not merely
        // empty of echo — they were never asked about.
        var grid = MooreGrid();
        double cell = 2.0 * grid.HalfWidthM / grid.Nx;

        for (int z = 0; z < grid.Nz; z++)
            for (int y = 0; y < grid.Ny; y++)
                for (int x = 0; x < grid.Nx; x++)
                {
                    double east = -grid.HalfWidthM + (x + 0.5) * cell;
                    double north = -grid.HalfWidthM + (y + 0.5) * cell;
                    if (Math.Sqrt(east * east + north * north) <= grid.HalfWidthM) continue;
                    Assert.Equal(0, grid.Voxels[grid.Index(x, y, z)]);
                }
    }

    [Fact]
    public void AVoxelHoldsTheValueOfTheBeamThatPassedThroughIt()
    {
        // Take a gate from the lowest cut, work out where it is in the grid, and check the
        // grid agrees. This is the whole resampling proved end to end on real data.
        var sweeps = Reflectivity(volumes.Moore).OrderBy(s => s.ElevationAngleDeg).ToList();
        var lowest = sweeps[0];
        double elevationRad = lowest.ElevationAngleDeg * Math.PI / 180.0;

        var grid = VolumeGrid3D.Build(sweeps, -30f, 80f, horizontalCells: 256, verticalCells: 48);
        double cell = 2.0 * grid.HalfWidthM / grid.Nx;
        double layer = (grid.TopHeightM - grid.BaseHeightM) / grid.Nz;

        int compared = 0, agreed = 0;
        for (int radial = 0; radial < lowest.RadialCount; radial += 37)
        {
            double azimuthRad = lowest.AzimuthsDeg[radial] * Math.PI / 180.0;
            for (int gate = 40; gate < lowest.GateCount; gate += 53)
            {
                float expected = lowest.Data[radial * lowest.GateCount + gate];
                if (float.IsNaN(expected) || expected < -20) continue;

                double slant = lowest.FirstGateM + gate * lowest.GateSpacingM;
                var (ground, height) = GeoMath.BeamPath(slant, elevationRad);
                if (ground > grid.HalfWidthM - cell) continue;
                if (height < grid.BaseHeightM || height > grid.TopHeightM - layer) continue;

                double east = ground * Math.Sin(azimuthRad);
                double north = ground * Math.Cos(azimuthRad);
                int ix = (int)((east + grid.HalfWidthM) / cell);
                int iy = (int)((north + grid.HalfWidthM) / cell);
                int iz = (int)((height - grid.BaseHeightM) / layer);
                if (ix < 0 || ix >= grid.Nx || iy < 0 || iy >= grid.Ny || iz < 0 || iz >= grid.Nz)
                    continue;

                byte voxel = grid.Voxels[grid.Index(ix, iy, iz)];
                if (voxel == 0) continue;   // the cell centre may sit between beams

                compared++;
                // A cell is over a kilometre across and the gate is a point inside it, so
                // exact equality is not the claim — being the same weather is.
                if (Math.Abs(grid.Decode(voxel) - expected) < 12f) agreed++;
            }
        }

        Assert.True(compared > 50, $"only {compared} gates were comparable");
        Assert.True(agreed / (double)compared > 0.85,
            $"only {agreed}/{compared} voxels matched the gate that passed through them");
    }

    [Fact]
    public void ZeroMeansUnsampledAndNotAWeakEcho()
    {
        // The encoding reserves 0 for "no beam came here". If a real value could encode to
        // 0 the renderer would punch holes through the middle of storms.
        var grid = MooreGrid();

        Assert.True(float.IsNaN(grid.Decode(0)));
        Assert.Equal(grid.EncodedMin, grid.Decode(1), 2);
        Assert.Equal(grid.EncodedMax, grid.Decode(255), 2);
    }

    [Fact]
    public void ValuesBelowTheEncodingFloorClampRatherThanWrapToEmpty()
    {
        // A gate at -32 dBZ with a floor of -30 must encode to 1, not to 0. Wrapping would
        // turn the weakest echo into a hole.
        var grid = VolumeGrid3D.Build(Reflectivity(volumes.Moore), 20f, 60f,
            horizontalCells: 64, verticalCells: 16);

        Assert.True(grid.FilledCount > 0);
        // Everything below 20 dBZ clamps to 1 rather than vanishing, so the filled count
        // with a high floor is no smaller than with a low one.
        var wide = VolumeGrid3D.Build(Reflectivity(volumes.Moore), -30f, 80f,
            horizontalCells: 64, verticalCells: 16);
        Assert.Equal(wide.FilledCount, grid.FilledCount);
    }

    [Fact]
    public void TheGridCarriesWhereAndWhenItCameFrom()
    {
        var grid = MooreGrid();

        Assert.Equal(Moment.Reflectivity, grid.Moment);
        Assert.InRange(grid.RadarLatDeg, 35.2, 35.4);      // KTLX
        Assert.InRange(grid.RadarLonDeg, -97.4, -97.2);
        Assert.Equal(2013, grid.ScanTimeUtc.Year);
        Assert.NotEmpty(grid.ElevationsUsed);
    }

    [Fact]
    public void AVolumeWithNoSweepsIsRefusedRatherThanReturningAnEmptyBox() =>
        Assert.Throws<ArgumentException>(() => VolumeGrid3D.Build([], -30f, 80f));

    [Fact]
    public void AnInvertedEncodingRangeIsRefused() =>
        Assert.Throws<ArgumentException>(() =>
            VolumeGrid3D.Build(Reflectivity(volumes.Moore), 80f, -30f));

    [Fact]
    public void AQuietVolumeProducesAMostlyEmptyBox()
    {
        // Fair weather. The grid must not manufacture something to look at.
        var grid = VolumeGrid3D.Build(Reflectivity(volumes.Quiet), -30f, 80f,
            horizontalCells: 96, verticalCells: 24);

        Assert.True(grid.FilledCount / (double)grid.Voxels.Length < 0.35,
            "a clear-air volume should not fill the box");
    }
}
