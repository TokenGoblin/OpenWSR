using OpenWSR.Geo;

namespace OpenWSR.Geo.Tests;

/// <summary>
/// Validated against the HRRR CONUS grid as ecCodes reports it: 1799x1059 at 3 km,
/// standard parallels 38.5/38.5, LoV -97.5.
/// </summary>
public class LambertConformalTests
{
    private static readonly LambertConformal Hrrr = new(38.5, 38.5, -97.5, 38.5);

    // Grid point (0,0) from ecCodes, and the corresponding grid steps.
    private const double Lat1 = 21.138123, Lon1 = -122.719528, StepM = 3000.0;
    private const int Nx = 1799;

    private static (double Lat, double Lon) GridPoint(int i, int j)
    {
        var origin = Hrrr.Forward(Lat1, Lon1);
        return Hrrr.Inverse(origin.X + i * StepM, origin.Y + j * StepM);
    }

    [Fact]
    public void RoundTripAtTheGridOrigin()
    {
        var (x, y) = Hrrr.Forward(Lat1, Lon1);
        var (lat, lon) = Hrrr.Inverse(x, y);
        Assert.Equal(Lat1, lat, 8);
        Assert.Equal(Lon1, lon, 8);
    }

    [Theory]
    // index, i, j, expected lat, expected lon — straight from ecCodes' grid iterator.
    [InlineData(1, 1, 0, 21.145110, -122.692861)]
    [InlineData(1798, 1798, 0, 21.140547, -72.289718)]
    [InlineData(1799, 0, 1, 21.162995, -122.727025)]
    [InlineData(952570, 952570 % Nx, 952570 / Nx, 38.497247, -97.505977)]
    [InlineData(1905140, 1905140 % Nx, 1905140 / Nx, 47.842195, -60.917193)]
    public void GridPointsMatchEcCodes(int index, int i, int j, double expectedLat, double expectedLon)
    {
        _ = index;
        var (lat, lon) = GridPoint(i, j);
        // 1e-4 degrees is ~11 m — well inside a 3 km grid cell.
        Assert.Equal(expectedLat, lat, 4);
        Assert.Equal(expectedLon, lon, 4);
    }

    [Fact]
    public void CentreOfGridSitsOnTheReferenceMeridian()
    {
        // Index 952570 is one row past the grid centre column, so it should sit within
        // a cell of LoV (-97.5) — a good check that the cone is oriented correctly.
        var (_, lon) = GridPoint(952570 % Nx, 952570 / Nx);
        Assert.InRange(lon, -97.55, -97.45);
    }
}
