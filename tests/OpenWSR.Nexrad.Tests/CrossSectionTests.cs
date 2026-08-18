using OpenWSR.Geo;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Cross-sections through the Moore 2013 volume. There is no external reference for
/// this, so the tests assert the physics: samples land in the right places, beams
/// climb with range, and the storm shows up where the plan-view says it should.
/// </summary>
public class CrossSectionTests(DecodedVolumes volumes) : IClassFixture<DecodedVolumes>
{
    private List<Sweep> ReflectivitySweeps() =>
        [.. volumes.Moore.Sweeps.Where(s => s.Moment == Moment.Reflectivity)];

    [Fact]
    public void SliceThroughTheStormFindsData()
    {
        var sweeps = ReflectivitySweeps();
        var radar = sweeps[0];
        // A 100 km line running west to east straight through the radar.
        var west = GeoMath.Offset(radar.RadarLatDeg, radar.RadarLonDeg, 3 * Math.PI / 2, 50_000);
        var east = GeoMath.Offset(radar.RadarLatDeg, radar.RadarLonDeg, Math.PI / 2, 50_000);

        var section = CrossSection.Build(sweeps, west.LatDeg, west.LonDeg, east.LatDeg, east.LonDeg);

        Assert.Equal(640, section.Width);
        Assert.Equal(220, section.Height);
        Assert.Equal(Moment.Reflectivity, section.Moment);
        Assert.Equal(100.0, section.LengthKm, 0);

        int sampled = section.Values.Count(v => !float.IsNaN(v));
        Assert.True(sampled > 5000, $"expected a well-populated slice, got {sampled} samples");

        var populated = section.Values.Where(v => !float.IsNaN(v)).ToArray();
        Assert.InRange(populated.Max(), 40f, 80f); // the supercell is on this line
        Assert.InRange(populated.Min(), -35f, 10f);
    }

    [Fact]
    public void LowBeamsCannotReachHighAltitudeAtCloseRange()
    {
        // Directly over the radar the lowest cuts are metres off the ground, so the
        // top of the slice must be empty there: no beam passes through 14 km at 1 km out.
        var sweeps = ReflectivitySweeps();
        var radar = sweeps[0];
        var a = GeoMath.Offset(radar.RadarLatDeg, radar.RadarLonDeg, 0, 1_000);
        var b = GeoMath.Offset(radar.RadarLatDeg, radar.RadarLonDeg, Math.PI, 1_000);

        var section = CrossSection.Build(sweeps, a.LatDeg, a.LonDeg, b.LatDeg, b.LonDeg, maxHeightKm: 15);

        // Top 10% of the image, middle columns — the cone of silence overhead.
        int filled = 0;
        for (int row = 0; row < section.Height / 10; row++)
            for (int column = section.Width / 3; column < section.Width * 2 / 3; column++)
                if (!float.IsNaN(section.Values[row * section.Width + column])) filled++;
        Assert.Equal(0, filled);
    }

    [Fact]
    public void SamplesFollowTheBeamUpwardWithRange()
    {
        // Along a radial the lowest cut rises with distance, so the bottom of the slice
        // should empty out at long range while data persists higher up.
        var sweeps = ReflectivitySweeps();
        var radar = sweeps[0];
        var near = GeoMath.Offset(radar.RadarLatDeg, radar.RadarLonDeg, 0, 5_000);
        var far = GeoMath.Offset(radar.RadarLatDeg, radar.RadarLonDeg, 0, 200_000);

        var section = CrossSection.Build(sweeps, near.LatDeg, near.LonDeg, far.LatDeg, far.LonDeg);

        int bottomRow = section.Height - 3; // ~200 m altitude
        int nearFilled = 0, farFilled = 0;
        for (int column = 0; column < 40; column++)
            if (!float.IsNaN(section.Values[bottomRow * section.Width + column])) nearFilled++;
        for (int column = section.Width - 40; column < section.Width; column++)
            if (!float.IsNaN(section.Values[bottomRow * section.Width + column])) farFilled++;

        Assert.True(nearFilled > farFilled,
            $"near-range low altitude should be better sampled ({nearFilled}) than far ({farFilled})");
    }

    [Fact]
    public void EmptySweepListIsRejected() =>
        Assert.Throws<ArgumentException>(() => CrossSection.Build([], 35, -97, 36, -97));
}
