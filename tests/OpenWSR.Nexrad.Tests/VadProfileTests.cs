using System.Collections;
using OpenWSR.Geo;
using OpenWSR.Nexrad;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// A VAD fit has an exact answer for a known wind, which makes these analytic rather than
/// approximate: build sweeps from a wind of chosen speed and direction, and the profile
/// must give that speed and direction back.
/// </summary>
public class VadProfileTests
{
    private const int Radials = 360;
    private const int Gates = 460;
    private const float FirstGateM = 2000;
    private const float GateSpacingM = 250;

    /// <summary>
    /// A sweep of a uniform horizontal wind. Radial velocity is the wind projected onto the
    /// beam: it grows with the cosine of the angle between them and shrinks with the cosine
    /// of the elevation, which is exactly what the fit has to undo.
    /// </summary>
    private static Sweep WindSweep(
        double speedMs, double fromDirectionDeg, float elevationDeg, float? nyquist = null)
    {
        double from = fromDirectionDeg * Math.PI / 180.0;
        // "From" direction to velocity components: a wind from the west blows toward the east.
        double u = -speedMs * Math.Sin(from);
        double v = -speedMs * Math.Cos(from);
        double cosElevation = Math.Cos(elevationDeg * Math.PI / 180.0);

        var data = new float[Radials * Gates];
        for (int r = 0; r < Radials; r++)
        {
            double azimuth = r * 2 * Math.PI / Radials;
            float radial = (float)((u * Math.Sin(azimuth) + v * Math.Cos(azimuth)) * cosElevation);
            for (int g = 0; g < Gates; g++) data[r * Gates + g] = radial;
        }

        return new Sweep(
            "KTLX", DateTime.UnixEpoch, 35.333, -97.278, 380,
            1, elevationDeg, Moment.Velocity,
            [.. Enumerable.Range(0, Radials).Select(i => i * 360f / Radials)],
            FirstGateM, GateSpacingM, Gates, data,
            new ScaleInfo(2f, 129f, 8),
            new BitArray(Radials * Gates),
            nyquist);
    }

    [Theory]
    [InlineData(20.0, 270.0)]   // from the west
    [InlineData(15.0, 0.0)]     // from the north, the wrap case
    [InlineData(30.0, 45.0)]    // from the north-east
    [InlineData(12.0, 180.0)]   // from the south
    [InlineData(25.0, 315.0)]
    public void RecoversAKnownWind(double speed, double fromDirection)
    {
        var profile = VadProfile.Compute(
            [WindSweep(speed, fromDirection, 0.5f), WindSweep(speed, fromDirection, 2.4f)]);

        Assert.NotEmpty(profile);
        foreach (var level in profile)
        {
            Assert.Equal(speed, level.SpeedMs, 1);
            // Directions wrap, so compare the signed separation rather than the values.
            double separation = Math.Abs(((level.DirectionDeg - fromDirection + 540) % 360) - 180);
            Assert.True(separation < 1.0,
                $"at {level.AltitudeM:F0} m expected {fromDirection}° got {level.DirectionDeg:F1}°");
        }
    }

    [Fact]
    public void HeightsClimbAndComeFromTheBeamGeometry()
    {
        var profile = VadProfile.Compute([WindSweep(20, 270, 0.5f), WindSweep(20, 270, 4.0f)]);

        Assert.NotEmpty(profile);
        // Strictly increasing, and each level is where its own cut actually reaches.
        for (int i = 1; i < profile.Count; i++)
            Assert.True(profile[i].AltitudeM > profile[i - 1].AltitudeM);

        foreach (var level in profile)
        {
            double elevation = level.ElevationDeg * Math.PI / 180.0;
            double slant = GeoMath.SlantRangeForHeight(level.AltitudeM, elevation);
            var (_, height) = GeoMath.BeamPath(slant, elevation);
            Assert.Equal(level.AltitudeM, height, 0);
        }
    }

    [Fact]
    public void AVeeringProfileIsResolvedLayerByLayer()
    {
        // Two cuts carrying different winds. The low cut reaches low altitudes and the
        // high cut reaches high ones, so the profile should show both — this is what makes
        // it a profile rather than a single number.
        var low = WindSweep(10, 180, 0.5f);   // from the south near the ground
        var high = WindSweep(30, 270, 6.0f);  // from the west aloft

        var profile = VadProfile.Compute([low, high], maxAltitudeM: 9000);
        Assert.NotEmpty(profile);

        var bottom = profile.First();
        var top = profile.Last();
        Assert.True(top.AltitudeM > bottom.AltitudeM);
        Assert.True(top.SpeedMs > bottom.SpeedMs,
            $"expected stronger flow aloft: {bottom.SpeedMs:F1} at {bottom.AltitudeM:F0} m, "
            + $"{top.SpeedMs:F1} at {top.AltitudeM:F0} m");
    }

    [Fact]
    public void AFoldedWindIsUnfoldedBeforeFitting()
    {
        // 40 m/s against a 25 m/s Nyquist folds. Left folded, the ring is discontinuous and
        // the fit would report a wind that was never blowing.
        const float nyquist = 25f;
        var truth = WindSweep(40, 270, 0.5f);

        var folded = new float[truth.Data.Length];
        for (int i = 0; i < truth.Data.Length; i++)
        {
            float interval = 2 * nyquist;
            float f = (truth.Data[i] + nyquist) % interval;
            if (f < 0) f += interval;
            folded[i] = f - nyquist;
        }

        var profile = VadProfile.Compute([truth with { Data = folded, NyquistMs = nyquist }]);
        Assert.NotEmpty(profile);
        Assert.Equal(40.0, profile[0].SpeedMs, 0);
    }

    [Fact]
    public void APartialRingIsRefusedRatherThanGuessed()
    {
        // Data in one quadrant only. A sine can be fitted through it, but it would be that
        // quadrant's velocities wearing a wind's clothes.
        var sweep = WindSweep(20, 270, 0.5f);
        var data = (float[])sweep.Data.Clone();
        for (int r = 0; r < Radials; r++)
            if (r is < 0 or > 90)
                for (int g = 0; g < Gates; g++) data[r * Gates + g] = float.NaN;

        Assert.Empty(VadProfile.Compute([sweep with { Data = data }]));
    }

    [Fact]
    public void NoiseWithNoWindInItIsRefused()
    {
        // Random velocities around the ring fit no sine; the residual check must catch it.
        var sweep = WindSweep(20, 270, 0.5f);
        var random = new Random(42);
        var data = new float[sweep.Data.Length];
        for (int i = 0; i < data.Length; i++) data[i] = (float)(random.NextDouble() * 60 - 30);

        var profile = VadProfile.Compute([sweep with { Data = data }]);
        Assert.True(profile.Count <= 2, $"noise produced {profile.Count} wind levels");
    }

    [Fact]
    public void AnEmptyOrNonVelocityInputGivesNothing()
    {
        Assert.Empty(VadProfile.Compute([]));
        var reflectivity = WindSweep(20, 270, 0.5f) with { Moment = Moment.Reflectivity };
        Assert.Empty(VadProfile.Compute([reflectivity]));
    }

    [Fact]
    public void SpeedIsAlsoOfferedInKnots()
    {
        var profile = VadProfile.Compute([WindSweep(20, 270, 0.5f)]);
        Assert.NotEmpty(profile);
        Assert.Equal(profile[0].SpeedMs * 1.943844, profile[0].SpeedKnots, 3);
    }
}
