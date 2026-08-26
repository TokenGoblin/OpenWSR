namespace OpenWSR.Nexrad.Analysis;

/// <summary>The wind at one height, as components rather than as a speed and a direction.</summary>
/// <remarks>
/// <paramref name="UMs"/> is positive toward the east and <paramref name="VMs"/> toward the
/// north — where the air is <em>going</em>. A <see cref="VadLevel"/> reports the direction the
/// wind comes <em>from</em>, so the conversion carries a negative sign that is easy to lose and
/// impossible to see afterwards: the hodograph simply comes out rotated by 180 degrees, which
/// still looks like a hodograph.
/// </remarks>
public readonly record struct HodographPoint(double AltitudeM, double UMs, double VMs);

/// <summary>
/// The wind profile read as a curve rather than a column of numbers, plus what that curve
/// says about rotation.
/// </summary>
/// <remarks>
/// A hodograph plots the wind vector at each height, tip to tip. Its <em>shape</em> is what
/// carries the information: length is shear, and curvature is the streamwise vorticity a
/// storm can tilt into rotation. That is not readable from a list of barbs, which is why
/// this exists alongside one rather than instead of it.
///
/// <para>Everything here works from heights alone. Storm-relative helicity needs no
/// thermodynamics, so it is available from a radar volume with nothing else — which is the
/// whole appeal, given the profile is already fitted from Level II velocity.</para>
/// </remarks>
public static class Hodograph
{
    /// <summary>Wind components at each fitted level, lowest first.</summary>
    public static IReadOnlyList<HodographPoint> FromProfile(IReadOnlyList<VadLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return levels
            .OrderBy(l => l.AltitudeM)
            .Select(l =>
            {
                double radians = l.DirectionDeg * Math.PI / 180.0;
                return new HodographPoint(
                    l.AltitudeM,
                    -l.SpeedMs * Math.Sin(radians),
                    -l.SpeedMs * Math.Cos(radians));
            })
            .ToList();
    }

    /// <summary>
    /// The part of the profile within <paramref name="depthM"/> of its base, interpolating a
    /// point at exactly that top when no level lands there.
    /// </summary>
    /// <remarks>
    /// Depth is measured from the <em>lowest fitted level</em>, not from sea level and not
    /// from the ground. A VAD profile starts wherever the lowest cut found enough echo — 120 m
    /// on one volume and 400 m on the next — so treating its base as zero is what makes
    /// "0–1 km" mean the same thing between scans. This is also what MetPy does with
    /// <c>with_agl=True</c>, which is what makes the two comparable.
    ///
    /// <para>Without the interpolated top, a layer would end at whatever level happened to be
    /// below the boundary, and the helicity of the last few hundred metres would be silently
    /// dropped or silently included depending on where the levels fell.</para>
    /// </remarks>
    public static IReadOnlyList<HodographPoint> Layer(
        IReadOnlyList<HodographPoint> points, double depthM, double bottomM = 0)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) return [];

        double baseM = points[0].AltitudeM;
        double from = baseM + bottomM;
        double to = baseM + bottomM + depthM;
        var layer = new List<HodographPoint>();

        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.AltitudeM < from)
            {
                // Interpolate the bottom when the layer starts between two levels.
                if (i + 1 < points.Count && points[i + 1].AltitudeM > from)
                    layer.Add(Interpolate(point, points[i + 1], from));
                continue;
            }

            if (point.AltitudeM > to)
            {
                if (i > 0) layer.Add(Interpolate(points[i - 1], point, to));
                break;
            }

            layer.Add(point);
        }

        return layer;
    }

    /// <summary>
    /// Storm-relative helicity over a layer, in m²/s².
    /// </summary>
    /// <remarks>
    /// The integral of the storm-relative wind crossed with the shear, which discretises to
    /// summing <c>u'[i+1]·v'[i] − u'[i]·v'[i+1]</c> over the layer. Positive and negative
    /// contributions are returned apart as well as summed, because they cancel: a hodograph
    /// that loops back on itself can total near zero while containing a great deal of both,
    /// and the total alone would report that as a benign profile.
    ///
    /// <para>Verified against MetPy 1.7.1 — see <c>HodographTests</c>. The storm motion is an
    /// input rather than something estimated here, because this application usually has a
    /// measured one: the SCIT algorithm reports the observed motion of the actual cell, which
    /// beats any estimate from the profile. <see cref="MeanWind"/> is the fallback when
    /// nothing is being tracked.</para>
    ///
    /// <para>Bunkers is deliberately not implemented. Its mean wind is
    /// <em>pressure</em>-weighted, and a radar wind profile has no pressure — synthesising one
    /// from a standard atmosphere would produce a number that agrees with a reference tool
    /// only because both were fed the same invention.</para>
    /// </remarks>
    public static (double Positive, double Negative, double Total) StormRelativeHelicity(
        IReadOnlyList<HodographPoint> points,
        double stormUMs, double stormVMs, double depthM, double bottomM = 0)
    {
        var layer = Layer(points, depthM, bottomM);
        double positive = 0, negative = 0;

        for (int i = 0; i + 1 < layer.Count; i++)
        {
            double u0 = layer[i].UMs - stormUMs, v0 = layer[i].VMs - stormVMs;
            double u1 = layer[i + 1].UMs - stormUMs, v1 = layer[i + 1].VMs - stormVMs;

            double term = u1 * v0 - u0 * v1;
            if (term > 0) positive += term;
            else if (term < 0) negative += term;
        }

        return (positive, negative, positive + negative);
    }

    /// <summary>
    /// The simple mean wind through a layer — the fallback storm motion when no cell is being
    /// tracked, and a reasonable one: a storm broadly goes with the flow it sits in.
    /// </summary>
    public static (double UMs, double VMs) MeanWind(
        IReadOnlyList<HodographPoint> points, double depthM, double bottomM = 0)
    {
        var layer = Layer(points, depthM, bottomM);
        if (layer.Count == 0) return (0, 0);
        return (layer.Average(p => p.UMs), layer.Average(p => p.VMs));
    }

    /// <summary>
    /// How deep the profile itself is: its top level above its base.
    /// </summary>
    /// <remarks>
    /// Callers must check this before labelling a result with a depth. <see cref="Layer"/>
    /// returns what it has when asked for more than exists, which is the right behaviour for
    /// a calculation and a trap for a caption: a VAD profile that fitted only to 2.7 km will
    /// happily yield a "0–6 km shear" that is nothing of the sort.
    /// </remarks>
    public static double DepthM(IReadOnlyList<HodographPoint> points) =>
        points.Count == 0 ? 0 : points[^1].AltitudeM - points[0].AltitudeM;

    /// <summary>Bulk shear across a layer: the vector from its bottom wind to its top wind.</summary>
    public static (double UMs, double VMs, double MagnitudeMs) BulkShear(
        IReadOnlyList<HodographPoint> points, double depthM, double bottomM = 0)
    {
        var layer = Layer(points, depthM, bottomM);
        if (layer.Count < 2) return (0, 0, 0);

        double du = layer[^1].UMs - layer[0].UMs;
        double dv = layer[^1].VMs - layer[0].VMs;
        return (du, dv, Math.Sqrt(du * du + dv * dv));
    }

    private static HodographPoint Interpolate(HodographPoint a, HodographPoint b, double altitudeM)
    {
        double span = b.AltitudeM - a.AltitudeM;
        double t = span <= 0 ? 0 : (altitudeM - a.AltitudeM) / span;
        return new HodographPoint(
            altitudeM,
            a.UMs + t * (b.UMs - a.UMs),
            a.VMs + t * (b.VMs - a.VMs));
    }
}
