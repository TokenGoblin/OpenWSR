namespace OpenWSR.Nexrad;

/// <summary>Radar data moments carried in Message 31.</summary>
public enum Moment
{
    /// <summary>REF — reflectivity, dBZ.</summary>
    Reflectivity,
    /// <summary>VEL — radial velocity, m/s.</summary>
    Velocity,
    /// <summary>SW — spectrum width, m/s.</summary>
    SpectrumWidth,
    /// <summary>ZDR — differential reflectivity, dB.</summary>
    DifferentialReflectivity,
    /// <summary>PHI — differential phase, degrees.</summary>
    DifferentialPhase,
    /// <summary>RHO — correlation coefficient, unitless.</summary>
    CorrelationCoefficient,
    /// <summary>CFP — clutter filter power removed, dB (build 19+).</summary>
    ClutterFilterPower,

    /// <summary>
    /// AZS — azimuthal shear, s⁻¹. Unlike the rest, this is <b>derived</b> rather than
    /// decoded: nothing in Message 31 carries it, and the decoder never produces it.
    /// <see cref="Analysis.AzimuthalShear"/> computes it from velocity on demand.
    /// </summary>
    AzimuthalShear,
}
