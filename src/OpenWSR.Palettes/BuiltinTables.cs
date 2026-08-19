namespace OpenWSR.Palettes;

/// <summary>Built-in tables modeled on the familiar NWS product colors.</summary>
public static class BuiltinTables
{
    public static ColorTable Reflectivity { get; } = ColorTable.FromStops("Reflectivity (dBZ)",
        (-30, new Rgba(0x00, 0x00, 0x00, 0x00)),
        (-10, new Rgba(0x40, 0x4A, 0x59, 0x60)),
        (5, new Rgba(0x33, 0x64, 0x70, 0xB0)),
        (10, new Rgba(0x41, 0xC0, 0xF0, 0xFF)),
        (18, new Rgba(0x2E, 0x77, 0xEE, 0xFF)),
        (22, new Rgba(0x2A, 0xFA, 0x30, 0xFF)),
        (35, new Rgba(0x0E, 0x8E, 0x12, 0xFF)),
        (40, new Rgba(0xFF, 0xFB, 0x1F, 0xFF)),
        (48, new Rgba(0xFF, 0xA6, 0x00, 0xFF)),
        (50, new Rgba(0xFF, 0x27, 0x0F, 0xFF)),
        (60, new Rgba(0xA4, 0x0B, 0x0B, 0xFF)),
        (65, new Rgba(0xFF, 0x2F, 0xF3, 0xFF)),
        (70, new Rgba(0x9B, 0x55, 0xE0, 0xFF)),
        (75, new Rgba(0xFF, 0xFF, 0xFF, 0xFF)));

    public static ColorTable Velocity { get; } = ColorTable.FromStops("Velocity (m/s)",
        (-35, new Rgba(0x0B, 0x61, 0x0B, 0xFF)),
        (-20, new Rgba(0x1E, 0xC2, 0x1E, 0xFF)),
        (-2, new Rgba(0xAF, 0xE3, 0xAF, 0xFF)),
        (0, new Rgba(0x8E, 0x8E, 0x9E, 0xB0)),
        (2, new Rgba(0xF0, 0xC0, 0xC0, 0xFF)),
        (20, new Rgba(0xE0, 0x30, 0x30, 0xFF)),
        (35, new Rgba(0x8A, 0x0A, 0x0A, 0xFF)));

    /// <summary>
    /// Velocity after unfolding, which can reach three times the Nyquist. The green/red
    /// core is unchanged so the picture still reads the same way; cyan and magenta mark
    /// the extremes, which by definition only exist because a fold was corrected.
    /// </summary>
    public static ColorTable DealiasedVelocity { get; } = ColorTable.FromStops("Velocity (m/s, unfolded)",
        (-80, new Rgba(0x00, 0xE0, 0xE0, 0xFF)),
        (-50, new Rgba(0x0B, 0x61, 0x0B, 0xFF)),
        (-25, new Rgba(0x1E, 0xC2, 0x1E, 0xFF)),
        (-2, new Rgba(0xAF, 0xE3, 0xAF, 0xFF)),
        (0, new Rgba(0x8E, 0x8E, 0x9E, 0xB0)),
        (2, new Rgba(0xF0, 0xC0, 0xC0, 0xFF)),
        (25, new Rgba(0xE0, 0x30, 0x30, 0xFF)),
        (50, new Rgba(0x8A, 0x0A, 0x0A, 0xFF)),
        (80, new Rgba(0xE0, 0x00, 0xE0, 0xFF)));

    /// <summary>
    /// Azimuthal shear in s⁻¹. Anticyclonic rotation is real but rarely what you are
    /// hunting, so it gets a muted blue while cyclonic runs through yellow to red. The
    /// scale tops out at 0.02 s⁻¹, around where a mesocyclone becomes a tornado warning.
    /// </summary>
    public static ColorTable AzimuthalShear { get; } = ColorTable.FromStops("Azimuthal shear (1/s)",
        (-0.020f, new Rgba(0x2A, 0x6C, 0xC8, 0xFF)),
        (-0.006f, new Rgba(0x6E, 0xA8, 0xE0, 0xFF)),
        (-0.002f, new Rgba(0x30, 0x36, 0x42, 0x30)),
        (0.002f, new Rgba(0x30, 0x36, 0x42, 0x30)),
        (0.006f, new Rgba(0xF2, 0xE3, 0x2A, 0xFF)),
        (0.012f, new Rgba(0xF2, 0x7A, 0x1E, 0xFF)),
        (0.020f, new Rgba(0xE0, 0x20, 0x20, 0xFF)));

    public static ColorTable SpectrumWidth { get; } = ColorTable.FromStops("Spectrum width (m/s)",
        (0, new Rgba(0x20, 0x28, 0x30, 0x80)),
        (4, new Rgba(0x3C, 0x8B, 0xC8, 0xFF)),
        (8, new Rgba(0xF2, 0xE3, 0x2A, 0xFF)),
        (12, new Rgba(0xF2, 0x54, 0x1E, 0xFF)),
        (15, new Rgba(0xFF, 0xFF, 0xFF, 0xFF)));

    public static ColorTable DifferentialReflectivity { get; } = ColorTable.FromStops("ZDR (dB)",
        (-4, new Rgba(0x28, 0x28, 0x3C, 0xC0)),
        (0, new Rgba(0x5A, 0x8C, 0xB4, 0xFF)),
        (1, new Rgba(0x2A, 0xC8, 0x2A, 0xFF)),
        (2, new Rgba(0xF2, 0xE3, 0x2A, 0xFF)),
        (4, new Rgba(0xF2, 0x54, 0x1E, 0xFF)),
        (8, new Rgba(0xC8, 0x28, 0xC8, 0xFF)));

    public static ColorTable CorrelationCoefficient { get; } = ColorTable.FromStops("RhoHV",
        (0.20f, new Rgba(0x20, 0x20, 0x28, 0xC0)),
        (0.70f, new Rgba(0x3C, 0x64, 0xC8, 0xFF)),
        (0.90f, new Rgba(0x2A, 0xC8, 0x2A, 0xFF)),
        (0.97f, new Rgba(0xF2, 0xE3, 0x2A, 0xFF)),
        (1.00f, new Rgba(0xF2, 0x54, 0x1E, 0xFF)),
        (1.05f, new Rgba(0xC8, 0x28, 0xC8, 0xFF)));

    public static ColorTable DifferentialPhase { get; } = ColorTable.FromStops("PhiDP (deg)",
        (0, new Rgba(0x20, 0x28, 0x30, 0xC0)),
        (90, new Rgba(0x3C, 0x8B, 0xC8, 0xFF)),
        (180, new Rgba(0x2A, 0xC8, 0x2A, 0xFF)),
        (270, new Rgba(0xF2, 0xE3, 0x2A, 0xFF)),
        (360, new Rgba(0xC8, 0x28, 0x28, 0xFF)));

    public static ColorTable For(OpenWSR.Nexrad.Moment moment) => moment switch
    {
        OpenWSR.Nexrad.Moment.Velocity => Velocity,
        OpenWSR.Nexrad.Moment.SpectrumWidth => SpectrumWidth,
        OpenWSR.Nexrad.Moment.DifferentialReflectivity => DifferentialReflectivity,
        OpenWSR.Nexrad.Moment.CorrelationCoefficient => CorrelationCoefficient,
        OpenWSR.Nexrad.Moment.DifferentialPhase => DifferentialPhase,
        OpenWSR.Nexrad.Moment.AzimuthalShear => AzimuthalShear,
        _ => Reflectivity,
    };
}
