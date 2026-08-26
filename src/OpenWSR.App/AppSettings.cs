using System.IO;
using System.Text.Json;

namespace OpenWSR.App;

/// <summary>
/// A place worth watching: home, work, wherever someone else is.
///
/// Each carries its own alert radius, because the question is not the same at each one. Fifty
/// miles around the house is useful lead time; fifty miles around an office you will leave in
/// an hour is noise.
/// </summary>
public sealed class SavedLocation
{
    public string Name { get; set; } = "Home";
    public double LatDeg { get; set; }
    public double LonDeg { get; set; }

    /// <summary>
    /// How the coordinates were arrived at — a map click, or a named Windows location
    /// provider. A GPS fix and an IP-address guess a state wide are both "your location" and
    /// should not read the same.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Radius Windows quoted for the fix, in metres; null for a hand-placed point.</summary>
    public double? AccuracyM { get; set; }

    /// <summary>Alert radius for this place. Null falls back to the global default.</summary>
    public double? AlertRadiusKm { get; set; }

    /// <summary>
    /// The one the app opens on and watches storms from. Exactly one location is primary; it
    /// decides the startup camera and which radar the storm layer follows, both of which need
    /// a single answer.
    /// </summary>
    public bool IsPrimary { get; set; }
}

/// <summary>User settings persisted at %LOCALAPPDATA%\OpenWSR\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Basemap style: <c>carto-dark</c>, <c>osm</c> or <c>maptiler</c>. Dark by default —
    /// reflectivity is a bright saturated palette and on the standard OSM style it competes
    /// with green landcover, blue water and orange roads for the same part of the eye.
    /// </summary>
    public string TileProvider { get; set; } = "carto-dark";
    public string? MapTilerKey { get; set; }

    /// <summary>Contact info appended to the User-Agent — api.weather.gov requires it.</summary>
    public string Contact { get; set; } = "";

    /// <summary>Places to watch for approaching storms. Empty until the user sets one.</summary>
    public List<SavedLocation> Locations { get; set; } = [];

    // ---- the single home this replaced ----
    //
    // Still deserialised, and migrated into Locations on load, because a settings file older
    // than the change carries a home here and nowhere else. Dropping them would silently
    // un-arm the alerts of everyone who already had one. They are written back as null so a
    // file saved by this version does not carry both spellings of the same fact.

    /// <summary>Obsolete: the single home location, migrated into <see cref="Locations"/>.</summary>
    public double? HomeLatDeg { get; set; }

    /// <summary>Obsolete: see <see cref="HomeLatDeg"/>.</summary>
    public double? HomeLonDeg { get; set; }

    /// <summary>Obsolete: see <see cref="HomeLatDeg"/>.</summary>
    public string? HomeSource { get; set; }

    /// <summary>Obsolete: see <see cref="HomeLatDeg"/>.</summary>
    public double? HomeAccuracyM { get; set; }

    /// <summary>The location the app opens on and watches storms from, or null if there are none.</summary>
    public SavedLocation? Primary =>
        Locations.FirstOrDefault(l => l.IsPrimary) ?? Locations.FirstOrDefault();

    /// <summary>This location's alert radius, or the global default where it has none.</summary>
    public double RadiusFor(SavedLocation location) => location.AlertRadiusKm ?? AlertRadiusKm;

    /// <summary>
    /// Fold a pre-list settings file's single home into <see cref="Locations"/>.
    ///
    /// Runs on every load rather than once behind a version flag: a file written by an older
    /// build can appear at any time — restored from a backup, synced from another machine —
    /// and the check is cheap. It is a no-op once the list holds anything.
    /// </summary>
    internal void MigrateLegacyHome()
    {
        if (Locations.Count > 0 || HomeLatDeg is not { } lat || HomeLonDeg is not { } lon)
        {
            ClearLegacyHome();
            return;
        }

        Locations.Add(new SavedLocation
        {
            Name = "Home",
            LatDeg = lat,
            LonDeg = lon,
            Source = HomeSource,
            AccuracyM = HomeAccuracyM,
            IsPrimary = true,
        });
        ClearLegacyHome();
    }

    private void ClearLegacyHome()
    {
        HomeLatDeg = null;
        HomeLonDeg = null;
        HomeSource = null;
        HomeAccuracyM = null;
    }

    /// <summary>
    /// Exactly one primary, always — the startup camera and the storm watch each need a single
    /// answer, and "none" and "two" are both ways of not having one.
    /// </summary>
    public void SetPrimary(SavedLocation location)
    {
        foreach (var l in Locations) l.IsPrimary = ReferenceEquals(l, location);
    }

    /// <summary>Keeps the primary valid after a removal, so the invariant survives editing.</summary>
    public void Remove(SavedLocation location)
    {
        Locations.Remove(location);
        if (Locations.Count > 0 && !Locations.Any(l => l.IsPrimary))
            Locations[0].IsPrimary = true;
    }

    /// <summary>Alert when a storm track or warning comes within this range of home.</summary>
    public double AlertRadiusKm { get; set; } = 40;

    /// <summary>
    /// How close a storm's track has to pass to count as coming for you rather than going by.
    /// Inside this, it interrupts; outside but still within <see cref="AlertRadiusKm"/>, it
    /// only appears in the list. A storm is not a point and the track is a forecast, so this
    /// is a few kilometres rather than zero.
    /// </summary>
    public double DirectHitRadiusKm { get; set; } = 8;

    /// <summary>Display units for distance, speed and height.</summary>
    public UnitSystem Units { get; set; } = UnitSystem.Imperial;

    /// <summary>Placefile sources (URLs or local paths) to reload at startup.</summary>
    public List<string> Placefiles { get; set; } = [];

    /// <summary>
    /// Imported GeoJSON and shapefile paths to reload at startup. A file that has since been
    /// moved or deleted is dropped from the list rather than reported: it is a layer someone
    /// added once, not a document they asked to open.
    /// </summary>
    public List<string> ImportedShapes { get; set; } = [];

    /// <summary>The shortcuts card is shown once, on the first run, and never nags again.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>Draw the WSR-88D site layer (hidden at national zoom regardless).</summary>
    public bool ShowSiteMarkers { get; set; } = true;

    /// <summary>
    /// The dBZ window the radar draws, defaulting to everything.
    /// </summary>
    /// <remarks>
    /// Whether clear-air return is context worth seeing or clutter worth hiding depends on
    /// what someone is watching for, so it is a control rather than a decision — but the
    /// shipped default hides nothing, because a product that silently omits data it was
    /// given is worse than a busy one.
    /// </remarks>
    public float DbzFilterMin { get; set; } = -30f;

    public float DbzFilterMax { get; set; } = 75f;

    /// <summary>
    /// Political boundaries as their own layer. Off by default: the first time either is
    /// switched on it downloads from the US Census, and a startup that reaches for the
    /// network before the user has asked for anything is the wrong default.
    /// </summary>
    public bool ShowStateLines { get; set; }

    public bool ShowCountyLines { get; set; }

    /// <summary>
    /// Which layer-panel sections are open, keyed by header.
    /// </summary>
    /// <remarks>
    /// The panel holds more than fits: 1758 px of content in a 914 px column before this was
    /// looked at. Which sections a given person needs open is not something a default can
    /// know — a chaser lives in STORMS and never opens SPC, and someone watching one town is
    /// the reverse — so the shipped defaults only have to be a reasonable start, and after
    /// that the panel remembers.
    /// </remarks>
    public Dictionary<string, bool> PanelSections { get; set; } = [];

    /// <summary>
    /// How many volumes an archive loop spans. This is a real cost, not a preference: every
    /// frame is a decoded sweep held in memory — about 5 MB for a super-res reflectivity cut
    /// — and each one has to be downloaded and decoded before the loop can play. Thirty is
    /// roughly two and a half hours of a five-minute VCP, which covers a storm's life
    /// without a long wait. See <see cref="ArchivePlaybackController.MaxLoopFrames"/>.
    /// </summary>
    public int LoopFrames { get; set; } = ArchivePlaybackController.DefaultLoopFrames;

    public string UserAgent =>
        string.IsNullOrWhiteSpace(Contact) ? "OpenWSR/0.1" : $"OpenWSR/0.1 ({Contact})";

    public static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenWSR");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
                loaded.MigrateLegacyHome();
                return loaded;
            }
        }
        catch (Exception)
        {
            // Corrupt settings fall back to defaults; a fresh file is written on save.
        }
        var settings = new AppSettings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
