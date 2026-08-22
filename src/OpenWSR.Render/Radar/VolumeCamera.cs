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
    /// <summary>
    /// The whole pose in one object.
    ///
    /// It is here because the camera is written from the UI thread and read from the render
    /// thread. Held as four fields, a pan concurrent with a frame can tear the twelve bytes
    /// of <see cref="TargetM"/> and the storm jumps for one frame. Held as a record and
    /// swapped whole, the render thread reads one reference and gets a pose that existed.
    /// </summary>
    private sealed record Pose(
        double AzimuthRad, double ElevationRad, double DistanceM, Vector3 TargetM);

    private static Pose Default => new(Math.PI, 22 * Math.PI / 180.0, 380_000, Vector3.Zero);

    /// <summary>
    /// Only ever written by the UI thread, so read-modify-write needs no lock; Volatile
    /// keeps the render thread from reading a stale reference indefinitely.
    /// </summary>
    private Pose _pose = Default;

    private Pose Read() => Volatile.Read(ref _pose);
    private void Write(Pose pose) => Volatile.Write(ref _pose, pose);

    /// <summary>Compass bearing of the eye from the target, radians clockwise from north.</summary>
    public double AzimuthRad => Read().AzimuthRad;   // looking north, from the south

    /// <summary>Height angle above the horizontal. Clamped short of the poles.</summary>
    public double ElevationRad => Read().ElevationRad;

    public double DistanceM => Read().DistanceM;

    public Vector3 TargetM => Read().TargetM;

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

    public void Reset(Vector3 targetM, double distanceM) =>
        Write(Default with
        {
            TargetM = targetM,
            DistanceM = Math.Clamp(distanceM, MinDistanceM, MaxDistanceM),
        });

    /// <summary>Drag to orbit. Pixels in, because that is what the mouse gives.</summary>
    public void Orbit(double dxPixels, double dyPixels)
    {
        const double radiansPerPixel = 0.006;
        var pose = Read();
        Write(pose with
        {
            AzimuthRad = pose.AzimuthRad - dxPixels * radiansPerPixel,
            ElevationRad = Math.Clamp(
                pose.ElevationRad + dyPixels * radiansPerPixel,
                -ElevationLimitRad, ElevationLimitRad),
        });
    }

    /// <summary>Wheel to close in. Multiplicative, so each notch feels the same at any range.</summary>
    public void Zoom(double notches)
    {
        var pose = Read();
        Write(pose with
        {
            DistanceM = Math.Clamp(
                pose.DistanceM * Math.Pow(0.85, notches), MinDistanceM, MaxDistanceM),
        });
    }

    /// <summary>
    /// Slide the target across the view plane. Scaled by distance so a drag moves the
    /// scene by the same amount on screen however far out the camera is.
    /// </summary>
    public void Pan(double dxPixels, double dyPixels, int viewportHeightPx)
    {
        if (viewportHeightPx <= 0) return;

        var pose = Read();
        var snapshot = Snapshot(pose, 1f);
        double metresPerPixel = 2.0 * pose.DistanceM * snapshot.TanHalfFov / viewportHeightPx;
        Write(pose with
        {
            TargetM = pose.TargetM
                    - (snapshot.Right * (float)(dxPixels * metresPerPixel)
                     - snapshot.Up * (float)(dyPixels * metresPerPixel)),
        });
    }

    public VolumeCameraSnapshot Snapshot(float aspect) => Snapshot(Read(), aspect);

    private VolumeCameraSnapshot Snapshot(Pose pose, float aspect)
    {
        double cosEl = Math.Cos(pose.ElevationRad), sinEl = Math.Sin(pose.ElevationRad);

        // Offset from target to eye. Azimuth is a compass bearing, so it turns from north
        // toward east — sin on x, cos on y, which is the opposite of the maths convention
        // and the same as every other bearing in this project.
        var offset = new Vector3(
            (float)(pose.DistanceM * cosEl * Math.Sin(pose.AzimuthRad)),
            (float)(pose.DistanceM * cosEl * Math.Cos(pose.AzimuthRad)),
            (float)(pose.DistanceM * sinEl));

        var eye = pose.TargetM + offset;
        var forward = Vector3.Normalize(-offset);

        // Right is horizontal by construction, so the horizon stays level however steep
        // the view gets. The elevation clamp guarantees this cross product is well defined.
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Cross(right, forward);

        return new VolumeCameraSnapshot(
            eye, forward, right, up, MathF.Tan(FieldOfViewRad * 0.5f), aspect);
    }
}
