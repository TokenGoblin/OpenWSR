using System.Numerics;

namespace OpenWSR.Render.Radar;

/// <summary>Everything the volume shader needs to build a ray for a pixel.</summary>
public readonly record struct VolumeCameraSnapshot(
    Vector3 Eye, Vector3 Forward, Vector3 Right, Vector3 Up,
    float TanHalfFov, float Aspect);

/// <summary>
/// An orbit camera: the storm stays put and the viewer walks around it.
///
/// A free-flying camera is the wrong tool for looking at one object — it is possible to
/// get lost inside a thunderstorm and have no idea which way is out. Orbiting cannot get
/// lost: there is always a target, and there is always a way back to looking at it.
///
/// World units are metres, x east, y north, z up.
/// </summary>
public sealed class VolumeCamera
{
    /// <summary>Compass bearing of the eye from the target, radians clockwise from north.</summary>
    public double AzimuthRad { get; private set; } = Math.PI;   // looking north, from the south

    /// <summary>Height angle above the horizontal. Clamped short of the poles.</summary>
    public double ElevationRad { get; private set; } = 22 * Math.PI / 180.0;

    public double DistanceM { get; private set; } = 380_000;

    public Vector3 TargetM { get; private set; }

    /// <summary>
    /// Never quite overhead and never quite underneath. At exactly ±90° the up vector and
    /// the view direction are parallel and the frame collapses — the picture spins on its
    /// own axis for no reason the user did anything to cause.
    /// </summary>
    private const double ElevationLimitRad = 88 * Math.PI / 180.0;

    public double MinDistanceM { get; set; } = 15_000;
    public double MaxDistanceM { get; set; } = 1_500_000;

    /// <summary>60° vertical field of view: wide enough to feel like a view, not a lens.</summary>
    public float FieldOfViewRad { get; set; } = 60f * MathF.PI / 180f;

    public void Reset(Vector3 targetM, double distanceM)
    {
        TargetM = targetM;
        DistanceM = Math.Clamp(distanceM, MinDistanceM, MaxDistanceM);
        AzimuthRad = Math.PI;
        ElevationRad = 22 * Math.PI / 180.0;
    }

    /// <summary>Drag to orbit. Pixels in, because that is what the mouse gives.</summary>
    public void Orbit(double dxPixels, double dyPixels)
    {
        const double radiansPerPixel = 0.006;
        AzimuthRad -= dxPixels * radiansPerPixel;
        ElevationRad = Math.Clamp(
            ElevationRad + dyPixels * radiansPerPixel, -ElevationLimitRad, ElevationLimitRad);
    }

    /// <summary>Wheel to close in. Multiplicative, so each notch feels the same at any range.</summary>
    public void Zoom(double notches) =>
        DistanceM = Math.Clamp(
            DistanceM * Math.Pow(0.85, notches), MinDistanceM, MaxDistanceM);

    /// <summary>
    /// Slide the target across the view plane. Scaled by distance so a drag moves the
    /// scene by the same amount on screen however far out the camera is.
    /// </summary>
    public void Pan(double dxPixels, double dyPixels, int viewportHeightPx)
    {
        if (viewportHeightPx <= 0) return;
        var snapshot = Snapshot(1f);
        double metresPerPixel = 2.0 * DistanceM * snapshot.TanHalfFov / viewportHeightPx;
        TargetM -= snapshot.Right * (float)(dxPixels * metresPerPixel)
                 - snapshot.Up * (float)(dyPixels * metresPerPixel);
    }

    public VolumeCameraSnapshot Snapshot(float aspect)
    {
        double cosEl = Math.Cos(ElevationRad), sinEl = Math.Sin(ElevationRad);

        // Offset from target to eye. Azimuth is a compass bearing, so it turns from north
        // toward east — sin on x, cos on y, which is the opposite of the maths convention
        // and the same as every other bearing in this project.
        var offset = new Vector3(
            (float)(DistanceM * cosEl * Math.Sin(AzimuthRad)),
            (float)(DistanceM * cosEl * Math.Cos(AzimuthRad)),
            (float)(DistanceM * sinEl));

        var eye = TargetM + offset;
        var forward = Vector3.Normalize(-offset);

        // Right is horizontal by construction, so the horizon stays level however steep
        // the view gets. The elevation clamp guarantees this cross product is well defined.
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Cross(right, forward);

        return new VolumeCameraSnapshot(
            eye, forward, right, up, MathF.Tan(FieldOfViewRad * 0.5f), aspect);
    }
}
