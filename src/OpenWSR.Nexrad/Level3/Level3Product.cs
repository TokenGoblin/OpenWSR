namespace OpenWSR.Nexrad.Level3;

/// <summary>A position relative to the radar: kilometers east (X) and north (Y).</summary>
public readonly record struct KmPoint(double XKm, double YKm);

/// <summary>One SCIT-tracked storm cell from an NST (product 58) file.</summary>
public sealed record StormCell(
    string Id,
    KmPoint Position,
    IReadOnlyList<KmPoint> PastPositions,      // most recent first
    IReadOnlyList<KmPoint> ForecastPositions); // nearest first

/// <summary>One hail detection (HDA) from an NHI (product 59) file. -999 = unknown.</summary>
public sealed record HailIndicator(
    string? StormId,
    KmPoint Position,
    int ProbabilityOfHail,        // percent
    int ProbabilityOfSevereHail,  // percent
    int MaxHailSizeInches);

/// <summary>One mesocyclone detection from an NMD (product 141) file.</summary>
public sealed record MesocycloneDetection(
    string? Id,
    KmPoint Position,
    double RadiusKm,
    int FeatureType,
    IReadOnlyList<KmPoint> PastPositions,
    IReadOnlyList<KmPoint> ForecastPositions);

/// <summary>Per-cell storm structure from an NSS (product 62) tabular block.</summary>
public sealed record StormCellStructure(
    string Id,
    double AzimuthDeg, double RangeNm, // cell position relative to the radar
    int MaxReflectivityDbz,
    double CellBasedVil,     // kg/m²
    double BaseKft, double TopKft, double MaxRefHeightKft);

/// <summary>A decoded Level III product: header metadata plus whichever feature lists apply.</summary>
public sealed record Level3Product(
    int ProductCode,
    string SiteId,             // 3-letter, from the WMO header (e.g. "TLX")
    double RadarLatDeg,
    double RadarLonDeg,
    double RadarHeightFt,
    int Vcp,
    int OperationalMode,
    DateTime VolumeTimeUtc,
    DateTime ProductTimeUtc,
    IReadOnlyList<StormCell> StormCells,
    IReadOnlyList<HailIndicator> HailIndicators,
    IReadOnlyList<MesocycloneDetection> Mesocyclones,
    IReadOnlyList<StormCellStructure> CellStructures);
