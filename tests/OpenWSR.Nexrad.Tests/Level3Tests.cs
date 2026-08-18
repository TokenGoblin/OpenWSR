using OpenWSR.Nexrad.Level3;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Level III golden-file tests against TLX 2021-10-11 01:49:20Z (active storm day).
/// Expected values produced by MetPy 1.7.1 Level3File on the same files.
/// </summary>
public class Level3Tests
{
    private static Level3Product Load(string product) =>
        Level3File.DecodeFile(TestData.Path($"level3/TLX_{product}_2021_10_11_01_49_20"));

    [Fact]
    public void Nst_HeaderMetadata()
    {
        var p = Load("NST");
        Assert.Equal(58, p.ProductCode);
        Assert.Equal("TLX", p.SiteId);
        Assert.Equal(35.333, p.RadarLatDeg, 3);
        Assert.Equal(-97.278, p.RadarLonDeg, 3);
        Assert.Equal(1277, p.RadarHeightFt);
        Assert.Equal(212, p.Vcp);
        Assert.Equal(2, p.OperationalMode);
        Assert.Equal(new DateTime(2021, 10, 11, 1, 49, 20), p.VolumeTimeUtc);
        Assert.Equal(new DateTime(2021, 10, 11, 1, 55, 25), p.ProductTimeUtc);
    }

    [Fact]
    public void Nst_54StormsWithTracks()
    {
        var p = Load("NST");
        Assert.Equal(54, p.StormCells.Count);

        var r5 = p.StormCells[0];
        Assert.Equal("R5", r5.Id);
        Assert.Equal(-5.75, r5.Position.XKm);
        Assert.Equal(-176.0, r5.Position.YKm);
        Assert.Equal(9, r5.PastPositions.Count);
        Assert.Equal(new KmPoint(-13.25, -180.0), r5.PastPositions[0]);
        Assert.Equal(new KmPoint(-76.0, -205.75), r5.PastPositions[^1]);
        Assert.Equal(4, r5.ForecastPositions.Count);
        Assert.Equal(new KmPoint(14.25, -167.5), r5.ForecastPositions[0]);
        Assert.Equal(new KmPoint(74.0, -141.75), r5.ForecastPositions[^1]);

        var o4 = p.StormCells[1];
        Assert.Equal("O4", o4.Id);
        Assert.Equal(new KmPoint(37.0, 4.25), o4.Position);
        Assert.Equal(9, o4.PastPositions.Count);
        Assert.Single(o4.ForecastPositions);
        Assert.Equal(new KmPoint(51.5, 13.0), o4.ForecastPositions[0]);

        // H2 (index 5 in MetPy order after Y0, G7, I0...) has no track packets at all.
        var h2 = p.StormCells.Single(c => c.Id == "H2");
        Assert.Equal(new KmPoint(34.0, -6.25), h2.Position);
    }

    [Fact]
    public void Nhi_HailDetections()
    {
        var p = Load("NHI");
        Assert.Equal(54, p.HailIndicators.Count);
        Assert.Empty(p.StormCells); // NHI's packet-15 groups are labels, not storms

        var r5 = p.HailIndicators[0];
        Assert.Equal("R5", r5.StormId);
        Assert.Equal(new KmPoint(-5.75, -176.0), r5.Position);
        Assert.Equal(100, r5.ProbabilityOfHail);
        Assert.Equal(40, r5.ProbabilityOfSevereHail);
        Assert.Equal(1, r5.MaxHailSizeInches);

        // Cells out of range for the hail algorithm carry -999 and no ID label.
        var unknown = p.HailIndicators.First(h => h.ProbabilityOfHail == -999);
        Assert.Null(unknown.StormId);
        Assert.Equal(new KmPoint(-27.25, -252.75), unknown.Position);

        var n6 = p.HailIndicators.Single(h => h.StormId == "N6");
        Assert.Equal(100, n6.ProbabilityOfHail);
        Assert.Equal(10, n6.ProbabilityOfSevereHail);
        Assert.Equal(0, n6.MaxHailSizeInches);
    }

    [Fact]
    public void Nss_CellStructuresWithMaxDbz()
    {
        var p = Load("NSS");
        Assert.Equal(62, p.ProductCode);
        // The header reports 54 detections but the table lists the 40 strongest cells.
        Assert.Equal(40, p.CellStructures.Count);

        var r5 = p.CellStructures.Single(c => c.Id == "R5");
        Assert.Equal(52, r5.MaxReflectivityDbz);
        Assert.Equal(37, r5.CellBasedVil);
        Assert.Equal(11.1, r5.BaseKft);
        Assert.Equal(50.1, r5.TopKft);
        Assert.Equal(32.5, r5.MaxRefHeightKft);

        var o4 = p.CellStructures.Single(c => c.Id == "O4");
        Assert.Equal(58, o4.MaxReflectivityDbz);
        Assert.Equal(35, o4.CellBasedVil);

        var n6 = p.CellStructures.Single(c => c.Id == "N6");
        Assert.Equal(56, n6.MaxReflectivityDbz);
    }

    [Fact]
    public void Nmd_MesocycloneDetections()
    {
        var p = Load("NMD");
        Assert.Equal(141, p.ProductCode);
        Assert.Equal(7, p.Mesocyclones.Count);

        var first = p.Mesocyclones[0];
        Assert.Equal("313", first.Id);
        Assert.Equal(new KmPoint(38.75, 5.0), first.Position);
        Assert.Equal(3.5, first.RadiusKm);
        Assert.Single(first.PastPositions);
        Assert.Equal(new KmPoint(35.25, -0.5), first.PastPositions[0]);
        Assert.Single(first.ForecastPositions);
        Assert.Equal(new KmPoint(41.75, 9.75), first.ForecastPositions[0]);

        var second = p.Mesocyclones[1];
        Assert.Equal("379", second.Id);
        Assert.Equal(new KmPoint(68.75, 172.75), second.Position);
        Assert.Equal(3.25, second.RadiusKm);
        Assert.Equal(6, second.PastPositions.Count);
        Assert.Equal(6, second.ForecastPositions.Count);
    }
}
