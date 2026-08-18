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
