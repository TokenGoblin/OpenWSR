using OpenWSR.App;

namespace OpenWSR.App.Tests;

/// <summary>
/// The map-centre readout. What is worth testing here is the "centred" rule: it is measured
/// in screen pixels, so the same ground offset has to read as centred when zoomed out and as
/// off-centre when zoomed in. Getting that backwards produces a control that says you are on
/// your house while showing you the next state.
/// </summary>
public class CentreReadoutTests
{
    private const double HomeLat = 40.7608, HomeLon = -111.8910;

    private static SavedLocation Home(string name = "Home", double lat = HomeLat, double lon = HomeLon) =>
        new() { Name = name, LatDeg = lat, LonDeg = lon, IsPrimary = true };

    [Fact]
    public void ExactCentreReadsAsOnThePlace()
    {
        var d = CentreReadout.Describe(HomeLat, HomeLon, 100, [Home()]);

        Assert.True(d.OnPlace);
        Assert.Equal("on Home", d.Relation);
        Assert.Equal("40.761, -111.891", d.Coordinates);
    }

    [Fact]
    public void TheSameOffsetIsCentredZoomedOutAndNotZoomedIn()
    {
        // ~11 km north of home, and a tolerance of 12 px either way of the given scale.
        const double offsetLat = 40.8608;

        // 2 km per pixel — national zoom, where 11 km is five pixels off the middle.
        var wide = CentreReadout.Describe(offsetLat, HomeLon, 2000, [Home()]);
        // 100 m per pixel — street zoom, where the same 11 km is far off the map.
        var close = CentreReadout.Describe(offsetLat, HomeLon, 100, [Home()]);

        Assert.True(wide.OnPlace);
        Assert.False(close.OnPlace);
    }

    [Fact]
    public void TheToleranceIsPixelsOnTheGroundNotMercatorMetres()
    {
        // Mercator metres-per-pixel overstate ground metres by 1/cos(latitude), so comparing a
        // great-circle distance straight against it silently widens the tolerance the further
        // north you are. The same offset and the same scale must not be centred in Alaska and
        // off-centre in Utah — the rule is stated in pixels, so it has to be enforced in them.
        const double offsetKm = 20.0;
        double degrees = offsetKm / 111.32; // one degree of latitude, near enough

        // 1400 m/px Mercator: ground is 1072 m/px at 40°N (tolerance 12.9 km, so 20 km is out)
        // and 679 m/px at 61°N (tolerance 8.1 km, further out still). Without the correction
        // both would compare against 16.8 km and Alaska would read as centred.
        var utah = CentreReadout.Describe(40.0 + degrees, -111.89, 1400,
            [Home("Utah", 40.0, -111.89)]);
        var alaska = CentreReadout.Describe(61.0 + degrees, -149.9, 1400,
            [Home("Alaska", 61.0, -149.9)]);

        Assert.False(utah.OnPlace);
        Assert.False(alaska.OnPlace);
    }

    [Fact]
    public void OffCentreNamesTheDistanceAndTheSideItIsOn()
    {
        // Due north of home, well outside the tolerance at this scale. The unit itself is
        // Units.System's business and is deliberately not asserted here — it is global state
        // that another test may legitimately be changing at the same moment.
        var d = CentreReadout.Describe(41.7608, HomeLon, 100, [Home()]);

        Assert.False(d.OnPlace);
        Assert.Contains(" N of Home", d.Relation);
        Assert.Matches(@"^\d+(\.\d+)? \w+ N of Home$", d.Relation);
    }

    [Fact]
    public void TheBearingIsOfTheCentreFromThePlaceNotTheOtherWayRound()
    {
        // Centre south of home must read "S of Home". Inverting this is silent and wrong.
        var d = CentreReadout.Describe(39.7608, HomeLon, 100, [Home()]);

        Assert.Contains(" S of Home", d.Relation);
    }

    [Fact]
    public void TheNearestSavedPlaceWinsNotThePrimaryOne()
    {
        var home = Home();
        var office = new SavedLocation { Name = "Office", LatDeg = 39.0, LonDeg = -111.891 };

        // Sitting on the office, with home 190 km away.
        var d = CentreReadout.Describe(39.0, -111.891, 100, [home, office]);

        Assert.True(d.OnPlace);
        Assert.Equal("on Office", d.Relation);
    }

    [Fact]
    public void WithNoSavedPlaceThereIsNothingToBeCentredOn()
    {
        var d = CentreReadout.Describe(HomeLat, HomeLon, 100, []);

        Assert.False(d.OnPlace);
        Assert.Equal("", d.Relation);
        Assert.Equal("40.761, -111.891", d.Coordinates);
    }

    [Fact]
    public void CoordinatesAreReportedForTheCentreEvenWhenItIsNowhereNearAPlace()
    {
        var d = CentreReadout.Describe(25.7743, -80.1937, 500, [Home()]);

        Assert.Equal("25.774, -80.194", d.Coordinates);
        Assert.False(d.OnPlace);
    }
}
