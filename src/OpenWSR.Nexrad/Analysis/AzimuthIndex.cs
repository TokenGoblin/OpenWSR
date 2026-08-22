namespace OpenWSR.Nexrad.Analysis;

/// <summary>
/// A lookup from azimuth to the radial that measured it.
///
/// A sweep's radials are not evenly spaced and not in any guaranteed order, so finding the
/// beam nearest an azimuth means a scan over every radial. That is fine once and ruinous
/// for a product that asks millions of times — a volume grid asks once per voxel. Binning
/// the ring finely enough that a bin is smaller than a beam turns the question into an
/// array index.
/// </summary>
public static class AzimuthIndex
{
    /// <summary>
    /// A tenth of a degree: comfortably finer than the 0.5° or 1° radial spacing, so a bin
    /// never spans two beams.
    /// </summary>
    public const int Bins = 3600;

    /// <summary>
    /// Build the table. Every bin resolves to a radial: the ring is swept once each way,
    /// and a bin keeps whichever of the two candidates is fewer bins distant.
    ///
    /// Sweeping only one way would answer with the radial at the largest azimuth at or
    /// below the query — a floor, not a nearest, biasing every consumer consistently in
    /// one direction by up to a full radial spacing. That is a quarter of a degree on a
    /// 0.5° sweep, which is 650 m of displacement at 150 km range.
    ///
    /// Both sweeps run twice around so the fill wraps: the radial nearest 359.9° is a
    /// candidate for 0.1°, and the reverse.
    /// </summary>
    public static int[] Build(Sweep sweep)
    {
        var seed = new int[Bins];
        Array.Fill(seed, -1);

        for (int radial = 0; radial < sweep.RadialCount; radial++)
        {
            int bin = (int)(sweep.AzimuthsDeg[radial] / 360.0 * Bins) % Bins;
            if (bin < 0) bin += Bins;
            seed[bin] = radial;
        }

        var index = new int[Bins];
        Array.Fill(index, -1);
        var distance = new int[Bins];
        Array.Fill(distance, int.MaxValue);

        // The first lap only primes the carried radial; the second is the one that records,
        // by which point a bin before the first seeded one has seen the radial behind it
        // across 0°.
        int carried = -1, carriedAt = 0;
        for (int i = 0; i < Bins * 2; i++)
        {
            int bin = i % Bins;
            if (seed[bin] >= 0) { carried = seed[bin]; carriedAt = i; }
            if (i < Bins || carried < 0) continue;

            int d = i - carriedAt;
            if (d < distance[bin]) { distance[bin] = d; index[bin] = carried; }
        }

        carried = -1; carriedAt = 0;
        for (int i = Bins * 2 - 1; i >= 0; i--)
        {
            int bin = i % Bins;
            if (seed[bin] >= 0) { carried = seed[bin]; carriedAt = i; }
            if (i >= Bins || carried < 0) continue;

            int d = carriedAt - i;
            // Strictly closer, so a bin equidistant between two radials keeps the one below
            // it and the table is the same every build.
            if (d < distance[bin]) { distance[bin] = d; index[bin] = carried; }
        }

        return index;
    }

    /// <summary>The radial nearest an azimuth in degrees, or -1 if the sweep had none.</summary>
    public static int RadialFor(int[] index, double azimuthDeg)
    {
        int bin = (int)(azimuthDeg / 360.0 * Bins) % Bins;
        if (bin < 0) bin += Bins;
        return index[bin];
    }
}
