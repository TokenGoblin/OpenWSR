namespace OpenWSR.Nexrad;

/// <summary>
/// What a VCP number implies about timing.
///
/// Level III products carry the VCP as a bare number with no timing in the file, so anything
/// that needs to know how far apart two consecutive volume scans were has to look it up.
/// Storm tracking is the case that matters: the SCIT past positions are one volume scan
/// apart, and reading them on the wrong time base misreports every storm's speed.
/// </summary>
public static class VolumeCoveragePattern
{
    /// <summary>
    /// Nominal minutes for one volume scan. These are the published nominal times, not what
    /// a given volume actually took — the RDA stretches a scan slightly to finish a cut —
    /// which is close enough for a speed derived from a quarter-kilometre centroid.
    ///
    /// The split runs along precipitation versus clear air: the precipitation VCPs sweep in
    /// four to six minutes, the clear-air ones take seven to ten because they dwell longer
    /// to find weak returns.
    /// </summary>
    public static double NominalScanMinutes(int vcp) => vcp switch
    {
        12 or 212 => 4.5,           // precipitation, fastest; the common convective choice
        11 or 211 => 5.0,
        112 => 5.5,
        21 or 121 or 215 or 221 => 6.0,
        35 => 7.0,                  // clear air, the shorter of the two
        31 or 32 => 10.0,           // clear air, deep dwell
        _ => 5.0,                   // unknown: assume precipitation mode, which is when
                                    // storm tracking has anything to say
    };
}
