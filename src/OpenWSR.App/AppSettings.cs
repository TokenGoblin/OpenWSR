using System.IO;
using System.Text.Json;

namespace OpenWSR.App;

/// <summary>User settings persisted at %LOCALAPPDATA%\OpenWSR\settings.json.</summary>
public sealed class AppSettings
{
    public string TileProvider { get; set; } = "osm";
    public string? MapTilerKey { get; set; }

    /// <summary>Contact info appended to the User-Agent — api.weather.gov requires it.</summary>
    public string Contact { get; set; } = "";

    /// <summary>Home location for proximity alerts; null until the user sets one.</summary>
    public double? HomeLatDeg { get; set; }
    public double? HomeLonDeg { get; set; }

    /// <summary>Alert when a storm track or warning comes within this range of home.</summary>
    public double AlertRadiusKm { get; set; } = 40;

    /// <summary>Display units for distance, speed and height.</summary>
    public UnitSystem Units { get; set; } = UnitSystem.Imperial;

    /// <summary>Placefile sources (URLs or local paths) to reload at startup.</summary>
    public List<string> Placefiles { get; set; } = [];

    /// <summary>The shortcuts card is shown once, on the first run, and never nags again.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>Draw the WSR-88D site layer (hidden at national zoom regardless).</summary>
    public bool ShowSiteMarkers { get; set; } = true;

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
                return JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
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
