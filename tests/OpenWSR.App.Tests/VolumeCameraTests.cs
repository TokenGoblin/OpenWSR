using System.Numerics;
using OpenWSR.Render.Radar;

namespace OpenWSR.App.Tests;

/// <summary>
/// The orbit camera for the 3D view. Worth testing rather than eyeballing: a camera that
/// is subtly wrong looks plausible in a screenshot and is maddening to use, and the two
/// ways it can break — a frame that collapses overhead, and a horizon that rolls — are
/// exactly the ones a still picture hides.
/// </summary>
public sealed class VolumeCameraTests
{
    private static VolumeCamera Framed()
    {
        var camera = new VolumeCamera();
        camera.Reset(new Vector3(0, 0, 20_000), 300_000);
        return camera;
    }

    [Fact]
    public void ItStartsSouthOfTheTargetLookingNorth()
    {
        // The orientation every plan view has trained a forecaster to expect: north away
        // from you. Starting anywhere else means reading the first frame twice.
        var snapshot = Framed().Snapshot(1.6f);

        Assert.True(snapshot.Eye.Y < 0, "the eye should start south of the target");
        Assert.True(snapshot.Forward.Y > 0, "the view should start looking north");
    }

    [Fact]
    public void TheEyeSitsAtTheRequestedDistanceFromTheTarget()
    {
        var camera = Framed();
        var snapshot = camera.Snapshot(1.6f);

        Assert.Equal(300_000, Vector3.Distance(snapshot.Eye, camera.TargetM), 0);
    }

    [Fact]
    public void ForwardAlwaysPointsAtTheTarget()
    {
        var camera = Framed();
        camera.Orbit(213, -47);

        var snapshot = camera.Snapshot(1.6f);
        var toTarget = Vector3.Normalize(camera.TargetM - snapshot.Eye);

        Assert.Equal(toTarget.X, snapshot.Forward.X, 4);
        Assert.Equal(toTarget.Y, snapshot.Forward.Y, 4);
        Assert.Equal(toTarget.Z, snapshot.Forward.Z, 4);
    }

    [Fact]
    public void TheFrameIsOrthonormalAtEveryAngle()
    {
        // A basis that drifts out of square shears the picture, which reads as the storm
        // being a shape it is not.
        var camera = Framed();

        for (int i = 0; i < 24; i++)
        {
            camera.Orbit(97, i % 2 == 0 ? 31 : -23);
            var s = camera.Snapshot(1.777f);

            Assert.Equal(1f, s.Forward.Length(), 4);
            Assert.Equal(1f, s.Right.Length(), 4);
            Assert.Equal(1f, s.Up.Length(), 4);
            Assert.Equal(0f, Vector3.Dot(s.Forward, s.Right), 4);
            Assert.Equal(0f, Vector3.Dot(s.Forward, s.Up), 4);
            Assert.Equal(0f, Vector3.Dot(s.Right, s.Up), 4);
        }
    }

    [Fact]
    public void TheHorizonNeverRolls()
    {
        // Right is built horizontal on purpose. If it ever tilts, the ground plane leans
        // and the whole view feels like it is falling over.
        var camera = Framed();

        for (int i = 0; i < 40; i++)
        {
            camera.Orbit(53, i % 3 == 0 ? 40 : -17);
            Assert.Equal(0f, camera.Snapshot(1.6f).Right.Z, 5);
        }
    }

    [Fact]
    public void ElevationStopsShortOfStraightOverhead()
    {
        // At exactly 90° the up vector and the view direction are parallel and the frame
        // collapses — the picture spins for no reason the user caused.
        var camera = Framed();
        camera.Orbit(0, 100_000);      // a drag far past the top

        Assert.True(camera.ElevationRad < Math.PI / 2);
        var s = camera.Snapshot(1.6f);
        Assert.Equal(1f, s.Right.Length(), 4);
        Assert.Equal(0f, s.Right.Z, 5);
    }

    [Fact]
    public void ElevationStopsShortOfStraightUnderneath()
    {
        var camera = Framed();
        camera.Orbit(0, -100_000);

        Assert.True(camera.ElevationRad > -Math.PI / 2);
        Assert.Equal(1f, camera.Snapshot(1.6f).Right.Length(), 4);
    }

    [Fact]
    public void OrbitingHalfATurnPutsTheEyeOnTheOppositeSide()
    {
        var camera = Framed();
        var before = camera.Snapshot(1.6f).Eye;

        // The orbit rate is 0.006 rad per pixel, so half a turn is pi / 0.006 pixels.
        camera.Orbit(Math.PI / 0.006, 0);
        var after = camera.Snapshot(1.6f).Eye;

        Assert.Equal(-(before.X - camera.TargetM.X), after.X - camera.TargetM.X, 0);
        Assert.Equal(-(before.Y - camera.TargetM.Y), after.Y - camera.TargetM.Y, 0);
        Assert.Equal(before.Z, after.Z, 0);
    }

    [Fact]
    public void ZoomingInAndBackOutReturnsToWhereItStarted()
    {
        var camera = Framed();
        double start = camera.DistanceM;

        camera.Zoom(4);
        Assert.True(camera.DistanceM < start);
        camera.Zoom(-4);

        Assert.Equal(start, camera.DistanceM, 0);
    }

    [Fact]
    public void ZoomIsClampedAtBothEnds()
    {
        var camera = Framed();

        camera.Zoom(500);
        Assert.Equal(camera.MinDistanceM, camera.DistanceM, 3);

        camera.Zoom(-500);
        Assert.Equal(camera.MaxDistanceM, camera.DistanceM, 3);
    }

    [Fact]
    public void ResetUndoesEveryOrbitAndZoom()
    {
        var camera = Framed();
        var original = camera.Snapshot(1.6f);

        camera.Orbit(431, -122);
        camera.Zoom(3);
        camera.Pan(90, 40, 800);
        camera.Reset(new Vector3(0, 0, 20_000), 300_000);

        var back = camera.Snapshot(1.6f);
        Assert.Equal(original.Eye.X, back.Eye.X, 1);
        Assert.Equal(original.Eye.Y, back.Eye.Y, 1);
        Assert.Equal(original.Eye.Z, back.Eye.Z, 1);
        Assert.Equal(Vector3.Zero.X, camera.TargetM.X, 3);
    }

    [Fact]
    public void PanningMovesTheTargetAcrossTheViewAndNotAlongIt()
    {
        // A pan slides the scene; it must never creep toward or away from the camera, or
        // the zoom level drifts every time the view is nudged.
        var camera = Framed();
        var before = camera.Snapshot(1.6f);
        var targetBefore = camera.TargetM;

        camera.Pan(120, 0, 900);
        var after = camera.Snapshot(1.6f);

        // The displacement, not the position: the target does not start at the origin.
        var moved = camera.TargetM - targetBefore;
        Assert.True(moved.Length() > 1, "the target should have moved");
        Assert.Equal(0f, Vector3.Dot(moved, before.Forward), 0);
        Assert.Equal(before.Forward.X, after.Forward.X, 4);   // and the view direction holds
    }

    [Fact]
    public void AWiderViewportWidensTheFrustumAndNotTheHeight()
    {
        // Aspect belongs on the horizontal axis only; putting it on both makes the field
        // of view change when the window is resized.
        var narrow = Framed().Snapshot(1.0f);
        var wide = Framed().Snapshot(2.0f);

        Assert.Equal(narrow.TanHalfFov, wide.TanHalfFov, 6);
        Assert.Equal(1.0f, narrow.Aspect, 6);
        Assert.Equal(2.0f, wide.Aspect, 6);
    }
}
