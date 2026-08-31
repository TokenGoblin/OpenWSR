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
    /// Basemap style: <c>osm-dark</c>, <c>osm</c>, <c>maptiler</c> or <c>usgs-topo</c>. Dark by
    /// default — reflectivity is a bright saturated palette and on the standard OSM style it
    /// competes with green landcover, blue water and orange roads for the same part of the eye.
    /// </summary>
    public string TileProvider { get; set; } = "osm-dark";
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
    /// Move a settings file off the retired CARTO basemap.
    ///
    /// <para>Unconditional, because there is no longer a working way to draw it: CARTO began
    /// requiring an API key in August 2026 and answers every unkeyed request with a valid PNG
    /// reading "API KEY REQUIRED". Leaving the saved choice alone would leave anyone who
    /// installed 0.1.0 staring at that for ever, since a changed default only reaches a file
    /// that does not have the setting. <c>osm-dark</c> is the nearest thing to what they
    /// chose — the same near-black ground, derived rather than fetched.</para>
    ///
    /// <para>Runs on every load, for the same reason <see cref="MigrateLegacyHome"/> does: an
    /// older file can appear at any time, restored from a backup or synced from another
    /// machine. It is a no-op once the value has moved.</para>
    /// </summary>
    internal void MigrateRetiredBasemap()
    {
        if (TileProvider is "carto-dark") TileProvider = "osm-dark";
    }

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

    // ---- running in the tray ----
    //
    // The alerting this app does is only worth anything while it is running, and a window is
    // a poor place to keep a watchman: it takes a screen, a taskbar button and a decision to
    // leave open. These three decide when OpenWSR stops being a window and becomes a tray
    // icon that is still watching.

    /// <summary>
    /// Closing the window leaves OpenWSR watching in the tray rather than exiting.
    ///
    /// On by default, which is a deliberate choice about what the close button means for this
    /// kind of app: a proximity alarm that quits when you tidy your desktop is a proximity
    /// alarm that is off when the weather arrives. It is made safe by the two things that
    /// always accompany it — a one-time notification saying where the app went and how to
    /// quit it (see <see cref="TrayHintShown"/>), and Exit in the tray menu — and by this
    /// switch, for anyone who wants the close button to mean close.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Minimising hides to the tray too, taking the taskbar button with it. Off by default:
    /// minimise has a well-understood meaning and quietly redefining it is worse than
    /// offering it, whereas the close button is being redefined for a reason.
    /// </summary>
    public bool MinimiseToTray { get; set; }

    /// <summary>Start straight into the tray, without showing the window.</summary>
    public bool StartInTray { get; set; }

    /// <summary>
    /// Whether the "OpenWSR is still running" balloon has been shown. The first time the
    /// window disappears into the tray is the only time it needs explaining; after that the
    /// user knows where it went, and repeating it is the app talking about itself while
    /// someone is trying to watch the weather.
    /// </summary>
    public bool TrayHintShown { get; set; }

    /// <summary>
    /// Whether the app has already asked Windows where this machine is.
    /// </summary>
    /// <remarks>
    /// Asked once, on the first run that has no saved place, and then never again on its own.
    /// The point is the "never again": an unpackaged desktop app gets no consent dialog when
    /// either of the two privacy switches is off — the call just comes back Denied — so
    /// retrying every launch would put the same red bar in front of someone who has already
    /// decided, every time they open the app. "Add my location" in Settings is always there
    /// for anyone who turns it on later.
    /// </remarks>
    public bool LocationAsked { get; set; }

    /// <summary>Draw the WSR-88D site layer (hidden at national zoom regardless).</summary>
    public bool ShowSiteMarkers { get; set; } = true;

    /// <summary>
    /// The dBZ window the radar draws. Opens at 20 dBZ, which is where rain starts.
    /// </summary>
    /// <remarks>
    /// Marshall-Palmer puts 20 dBZ at 0.026 in/hr — light rain reaching the ground — while
    /// 10 dBZ is 0.006 in/hr, which is not rain at all but insects, birds and dust. So the
    /// default is not hiding weather, it is hiding the things that are not weather, and it
    /// agrees with <c>GateQuality.DefaultMinReflectivityDbz</c>, chosen separately and for
    /// a different reason.
    ///
    /// <para>This did start at −30, on the principle that a product silently omitting data
    /// it was given is worse than a busy one. That principle is right and the default still
    /// broke it, so it is worth being clear about what changed: a floor is only an omission
    /// if what it removes is weather. What it actually costs is <b>snow</b>, which returns
    /// far less energy per unit water — 15 dBZ of snow can be accumulating steadily and this
    /// default hides it. The panel says so, next to the slider, because that is where someone
    /// needs to know it.</para>
    /// </remarks>
    public float DbzFilterMin { get; set; } = 20f;

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
    /// Layer checkboxes by control name, and the sliders beside them.
    /// </summary>
    /// <remarks>
    /// Dictionaries rather than thirty typed properties, and hooked by walking the panel
    /// rather than by wiring thirty handlers, for the same reason <see cref="PanelSections"/>
    /// is: a layer added later is remembered without anyone remembering to make it so. It
    /// also means the panel does not end up half-remembered, which is where this started —
    /// which <em>sections</em> were open survived a restart while every toggle inside them
    /// reset.
    ///
    /// <para>Site, product and tilt are deliberately absent. Those are where you were looking
    /// rather than how you like the app set up, and reopening on velocity at tilt four over
    /// a site investigated last week is worse than opening on reflectivity at home.</para>
    /// </remarks>
    public Dictionary<string, bool> LayerToggles { get; set; } = [];

    public Dictionary<string, double> LayerSliders { get; set; } = [];

    /// <summary>
    /// How many volumes an archive loop spans. This is a real cost, not a preference: every
    /// frame is a decoded sweep held in memory — about 5 MB for a super-res reflectivity cut
    /// — and each one has to be downloaded and decoded before the loop can play. Thirty is
    /// roughly two and a half hours of a five-minute VCP, which covers a storm's life
    /// without a long wait. See <see cref="ArchivePlaybackController.MaxLoopFrames"/>.
    /// </summary>
    public int LoopFrames { get; set; } = ArchivePlaybackController.DefaultLoopFrames;

    /// <summary>
    /// Sent on every outbound request. The project URL is in it because OSM's tile usage
    /// policy asks an application to be identifiable, and since the dark basemap stopped being
    /// CARTO's, <c>tile.openstreetmap.org</c> carries every install — an anonymous User-Agent
    /// there is one that gets blocked rather than contacted. api.weather.gov and Nominatim
    /// both require a descriptive one too.
    /// </summary>
    public string UserAgent =>
        string.IsNullOrWhiteSpace(Contact)
            ? "OpenWSR/0.1 (+https://github.com/TokenGoblin/OpenWSR)"
            : $"OpenWSR/0.1 (+https://github.com/TokenGoblin/OpenWSR; {Contact})";

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
                loaded.MigrateRetiredBasemap();
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
