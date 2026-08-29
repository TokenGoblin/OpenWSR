using OpenWSR.Ingest;

namespace OpenWSR.App.Tests;

/// <summary>
/// Ranking for the hotspot finder. The network scan is not exercised here — it needs 163
/// live products — but the choice made from its results is pure, and it is the part that
/// decides where the camera ends up.
///
/// The trap the threshold exists for: DVL reports something almost everywhere, so a naive
/// "nearest return" is reliably an insect swarm or a patch of virga a few miles away rather
/// than weather. The trap the fallback exists for is the opposite one — on a genuinely quiet
/// day nothing clears the bar and the button must still do something.
/// </summary>
public class HotspotSelectionTests
{
    // Salt Lake City, and the site the cells are attributed to unless a test says otherwise.
    private const double OriginLat = 40.77, OriginLon = -111.89;

    private static readonly RadarSite Kmtx =
        new("KMTX", "Salt Lake City", "UT", 41.263, -112.448, 1969, false);

    private static Hotspot Cell(double vil, double lat, double lon, RadarSite? site = null) =>
        new(site ?? Kmtx, vil, lat, lon, 50, new DateTime(2026, 8, 29, 16, 20, 0, DateTimeKind.Utc));

    [Fact]
    public void NearestRealStormWinsOverAHeavierDistantOne()
    {
        // Distinct sites, so asserting on the site proves something. Both cells defaulting to
        // KMTX made the Assert.Same below hold whichever one was picked.
        var near = Cell(6.0, 41.05, -111.89);    // ~31 km north
        var heavy = Cell(45.0, 39.00, -111.89,   // ~197 km south
            new RadarSite("KICX", "Cedar City", "UT", 37.591, -112.862, 3250, false));

        var pick = NationalStormScan.SelectNearest([heavy, near], OriginLat, OriginLon);

        Assert.Same(near.Site, pick!.Site);
        Assert.Equal("KMTX", pick.Site.Icao);
        Assert.Equal(6.0, pick.MaxVilKgM2);
        Assert.InRange(pick.DistanceKm!.Value, 28, 34);
    }

    [Fact]
    public void DrizzleIsIgnoredEvenWhenItIsTheClosestThing()
    {
        var drizzle = Cell(0.8, 40.80, -111.89); // ~3 km, well under the bar
        var storm = Cell(6.0, 41.05, -111.89);   // ~31 km

        var pick = NationalStormScan.SelectNearest([drizzle, storm], OriginLat, OriginLon);

        Assert.Equal(6.0, pick!.MaxVilKgM2);
    }

    [Fact]
    public void FallsBackToTheHeaviestWhenNothingAnywhereIsRaining()
    {
        var faint = Cell(0.8, 40.80, -111.89);
        var slightlyLessFaint = Cell(1.2, 39.00, -111.89);

        var pick = NationalStormScan.SelectNearest([faint, slightlyLessFaint], OriginLat, OriginLon);

        // Heaviest of a quiet set, and still carrying its distance so the caller can say how far.
        Assert.Equal(1.2, pick!.MaxVilKgM2);
        Assert.NotNull(pick.DistanceKm);
    }

    [Fact]
    public void DistanceIsMeasuredToTheCellNotToItsRadar()
    {
        // A site right on top of us whose peak cell is 200 km away, against a site far off
        // whose cell is nearly overhead. Ranking on the radar would pick the wrong one.
        var closeSiteFarCell = Cell(10.0, 39.00, -111.89, new RadarSite("KAAA", "Close", "UT", 40.77, -111.89, 0, false));
        var farSiteCloseCell = Cell(10.0, 40.90, -111.89, new RadarSite("KZZZ", "Far", "NV", 36.00, -115.00, 0, false));

        var pick = NationalStormScan.SelectNearest([closeSiteFarCell, farSiteCloseCell], OriginLat, OriginLon);

        Assert.Equal("KZZZ", pick!.Site.Icao);
    }

    [Fact]
    public void AnEmptyScanSelectsNothing()
    {
        Assert.Null(NationalStormScan.SelectNearest([], OriginLat, OriginLon));
        Assert.Null(NationalStormScan.Heaviest([]));
    }

    [Fact]
    public void HeaviestStillIgnoresDistanceEntirely()
    {
        var near = Cell(6.0, 41.05, -111.89);
        var heavy = Cell(45.0, 39.00, -111.89);

        // The Shift-click path: unchanged, and deliberately blind to where you are.
        Assert.Equal(45.0, NationalStormScan.Heaviest([near, heavy])!.MaxVilKgM2);
    }

    [Fact]
    public void TheThresholdIsAFloorOnExistenceNotOnSeverity()
    {
        // A plain rain shower has to qualify — the button is "nearest precipitation", not
        // "nearest severe storm". 4 kg/m² is ordinary rain and must clear the bar.
        var shower = Cell(4.0, 41.05, -111.89);

        Assert.True(shower.MaxVilKgM2 >= NationalStormScan.SignificantVilKgM2);
        Assert.Equal(4.0, NationalStormScan.SelectNearest([shower], OriginLat, OriginLon)!.MaxVilKgM2);
    }
}
