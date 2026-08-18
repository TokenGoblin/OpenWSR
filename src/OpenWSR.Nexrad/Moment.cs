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
}
