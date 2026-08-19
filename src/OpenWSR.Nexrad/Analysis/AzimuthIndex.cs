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
    /// Build the table. Every bin resolves to a radial — gaps are filled outward from the
    /// bins that do have one, twice around the ring so the fill wraps in both directions
    /// and the radial nearest 359.9° is not the one nearest 0.1°.
    /// </summary>
    public static int[] Build(Sweep sweep)
    {
        var index = new int[Bins];
        Array.Fill(index, -1);

        for (int radial = 0; radial < sweep.RadialCount; radial++)
        {
            int bin = (int)(sweep.AzimuthsDeg[radial] / 360.0 * Bins) % Bins;
            if (bin < 0) bin += Bins;
            index[bin] = radial;
        }

        for (int pass = 0; pass < 2; pass++)
        {
            int last = -1;
            for (int i = 0; i < Bins * 2; i++)
            {
                int bin = i % Bins;
                if (index[bin] >= 0) last = index[bin];
                else if (last >= 0) index[bin] = last;
            }
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
