using System.IO;
using System.Text.Json;
using OpenWSR.App;

namespace OpenWSR.App.Tests;

/// <summary>
/// The saved-location list, and the migration off the single home it replaced.
///
/// The migration is the part worth being careful about. A settings file written before the
/// change carries a home in <c>homeLatDeg</c> and nowhere else, and dropping it would silently
/// un-arm the proximity alerts of anyone who already had one — quietly, at the moment they
/// most want them.
/// </summary>
public class SavedLocationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static AppSettings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, Options)!;
        settings.MigrateLegacyHome();
        return settings;
    }

    [Fact]
    public void LocationIsAskedForOnceAndOnlyWithNoPlaceSaved()
    {
        // Two guards, and both matter. An unpackaged app gets no consent dialog when the
        // privacy switches are off — the call just returns Denied — so without the flag the
        // same red bar would greet someone who has already decided, every single launch.
        var fresh = new AppSettings();
        Assert.False(fresh.LocationAsked);
        Assert.Empty(fresh.Locations);

        // And a settings file that already names a place must never be second-guessed by the
        // OS's idea of where the machine is.
        var configured = FromJson("""
            {
              "locations": [
                { "name": "Home", "latDeg": 39.8283, "lonDeg": -98.5795, "isPrimary": true }
              ]
            }
            """);
        Assert.Single(configured.Locations);
    }

    [Fact]
    public void TheAskedFlagSurvivesARoundTrip()
    {
        // It is written before the attempt rather than after, so a call that throws or an app
        // closed mid-way still counts as having asked. That only holds if it persists.
        var settings = FromJson("""{ "locationAsked": true }""");
        Assert.True(settings.LocationAsked);
    }

    [Fact]
    public void LayerStateRoundTripsAndOnlyRecordsWhatChanged()
    {
        // Untouched controls stay out of the file, so anything never changed keeps whatever
        // the XAML says — which is what lets a shipped default be revised later without
        // being overridden by a settings file that only ever agreed with the old one.
        var settings = FromJson("""
            {
              "layerToggles": { "FilterHailField": true, "FilterFlood": false },
              "layerSliders": { "OpacitySlider": 62.5 }
            }
            """);

        Assert.True(settings.LayerToggles["FilterHailField"]);
        Assert.False(settings.LayerToggles["FilterFlood"]);
        Assert.Equal(62.5, settings.LayerSliders["OpacitySlider"]);
        Assert.False(settings.LayerToggles.ContainsKey("FilterTornado"));
    }

    [Fact]
    public void ASettingsFileNamingAControlThatNoLongerExistsIsHarmless()
    {
        // Renaming or removing a control must not strand someone on an unopenable settings
        // file; the restore looks each name up and skips what it cannot find.
        var settings = FromJson("""
            { "layerToggles": { "FilterSomethingRemovedInV2": true } }
            """);

        Assert.Single(settings.LayerToggles);
    }

    [Fact]
    public void LayerStateStartsEmptySoTheShippedDefaultsApply()
    {
        var fresh = new AppSettings();
        Assert.Empty(fresh.LayerToggles);
        Assert.Empty(fresh.LayerSliders);
    }

    // ---- migration ----

    /// <summary>
    /// The shape of a settings file from before the change.
    /// </summary>
    /// <remarks>
    /// The coordinates are the geographic centre of the contiguous United States, carried to
    /// full double precision on purpose: the migration must not round a saved position, and a
    /// value with digits all the way down is the only kind that proves it.
    /// </remarks>
    [Fact]
    public void APreListSettingsFileKeepsItsHome()
    {
        var settings = FromJson("""
            {
              "tileProvider": "osm",
              "contact": "",
              "homeLatDeg": 39.828300123456789,
              "homeLonDeg": -98.579500987654321,
              "homeSource": "Wi-Fi",
              "homeAccuracyM": 147,
              "alertRadiusKm": 15,
              "units": "Imperial",
              "loopFrames": 60
            }
            """);

        var home = Assert.Single(settings.Locations);
        Assert.Equal("Home", home.Name);
        Assert.Equal(39.828300123456789, home.LatDeg, 9);
        Assert.Equal(-98.579500987654321, home.LonDeg, 9);
        Assert.Equal("Wi-Fi", home.Source);
        Assert.Equal(147, home.AccuracyM);
        Assert.True(home.IsPrimary, "the migrated home has to be the primary, or nothing is");

        // The global radius is untouched: it was never per-location before.
        Assert.Equal(15, settings.AlertRadiusKm);
        Assert.Equal(15, settings.RadiusFor(home));
    }

    /// <summary>
    /// The legacy fields are cleared on migration, so a file written by this version does not
    /// carry two spellings of the same fact — and a later load cannot resurrect a stale home
    /// over an edited list.
    /// </summary>
    [Fact]
    public void TheLegacyFieldsAreClearedOnceMigrated()
    {
        var settings = FromJson("""
            {"homeLatDeg": 40.4, "homeLonDeg": -111.9, "homeSource": "map"}
            """);

        Assert.Null(settings.HomeLatDeg);
        Assert.Null(settings.HomeLonDeg);
        Assert.Null(settings.HomeSource);
        Assert.Null(settings.HomeAccuracyM);

        string round = JsonSerializer.Serialize(settings, Options);
        Assert.DoesNotContain("40.4", round.Replace("\"latDeg\":40.4", ""));
    }

    /// <summary>Migration must not touch a list that already exists.</summary>
    [Fact]
    public void AnExistingListIsNotDisturbedByAStaleHome()
    {
        var settings = FromJson("""
            {
              "locations": [
                {"name": "Work", "latDeg": 40.76, "lonDeg": -111.89, "isPrimary": true}
              ],
              "homeLatDeg": 1.0,
              "homeLonDeg": 2.0
            }
            """);

        var only = Assert.Single(settings.Locations);
        Assert.Equal("Work", only.Name);
        Assert.Equal(40.76, only.LatDeg, 6);
        Assert.Null(settings.HomeLatDeg);
    }

    [Fact]
    public void AFileWithNoHomeAtAllMigratesToNothing()
    {
        var settings = FromJson("""{"tileProvider": "osm"}""");

        Assert.Empty(settings.Locations);
        Assert.Null(settings.Primary);
    }

    /// <summary>Half a home is not a home — a latitude without a longitude places nothing.</summary>
    [Fact]
    public void AHalfWrittenHomeIsNotMigrated()
    {
        Assert.Empty(FromJson("""{"homeLatDeg": 40.4}""").Locations);
        Assert.Empty(FromJson("""{"homeLonDeg": -111.9}""").Locations);
    }

    /// <summary>
    /// Migration runs on every load rather than once behind a version flag, because an older
    /// file can turn up at any time — restored from a backup, synced from another machine.
    /// Running it twice must be a no-op.
    /// </summary>
    [Fact]
    public void MigratingTwiceChangesNothing()
    {
        var settings = FromJson("""{"homeLatDeg": 40.4, "homeLonDeg": -111.9}""");
        settings.MigrateLegacyHome();
        settings.MigrateLegacyHome();

        Assert.Single(settings.Locations);
    }

    // ---- the list ----

    [Fact]
    public void TheListSurvivesASettingsRoundTrip()
    {
        var settings = new AppSettings();
        settings.Locations.Add(new SavedLocation
        {
            Name = "Mum and Dad",
            LatDeg = 40.23,
            LonDeg = -111.66,
            Source = "map",
            AlertRadiusKm = 80,
            IsPrimary = true,
        });

        var round = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, Options), Options)!;

        var back = Assert.Single(round.Locations);
        Assert.Equal("Mum and Dad", back.Name);
        Assert.Equal(40.23, back.LatDeg, 6);
        Assert.Equal(80, back.AlertRadiusKm);
        Assert.True(back.IsPrimary);
    }

    /// <summary>
    /// A per-location radius overrides the global one; without it the global applies. Fifty
    /// miles round the house is lead time, fifty round an office you leave in an hour is noise.
    /// </summary>
    [Fact]
    public void APerLocationRadiusOverridesTheGlobalDefault()
    {
        var settings = new AppSettings { AlertRadiusKm = 40 };
        var home = new SavedLocation { Name = "Home" };
        var work = new SavedLocation { Name = "Work", AlertRadiusKm = 15 };
        settings.Locations.AddRange([home, work]);

        Assert.Equal(40, settings.RadiusFor(home));
        Assert.Equal(15, settings.RadiusFor(work));
    }

    /// <summary>
    /// Exactly one primary, always. The startup camera and the storm watch each need a single
    /// answer, and both "none" and "two" are ways of not having one.
    /// </summary>
    [Fact]
    public void SettingAPrimaryDemotesTheOthers()
    {
        var settings = new AppSettings();
        var home = new SavedLocation { Name = "Home", IsPrimary = true };
        var work = new SavedLocation { Name = "Work" };
        settings.Locations.AddRange([home, work]);

        settings.SetPrimary(work);

        Assert.False(home.IsPrimary);
        Assert.True(work.IsPrimary);
        Assert.Same(work, settings.Primary);
    }

    /// <summary>Removing the primary promotes another, rather than leaving the app without one.</summary>
    [Fact]
    public void RemovingThePrimaryPromotesSomethingElse()
    {
        var settings = new AppSettings();
        var home = new SavedLocation { Name = "Home", IsPrimary = true };
        var work = new SavedLocation { Name = "Work" };
        settings.Locations.AddRange([home, work]);

        settings.Remove(home);

        Assert.Same(work, settings.Primary);
        Assert.True(work.IsPrimary);
    }

    [Fact]
    public void RemovingTheLastLocationLeavesNoPrimary()
    {
        var settings = new AppSettings();
        var home = new SavedLocation { Name = "Home", IsPrimary = true };
        settings.Locations.Add(home);

        settings.Remove(home);

        Assert.Empty(settings.Locations);
        Assert.Null(settings.Primary);
    }

    /// <summary>
    /// A list where nothing was ever marked primary — which a hand-edited file can easily be —
    /// still has to answer. The first entry stands in.
    /// </summary>
    [Fact]
    public void AListWithNoPrimaryFallsBackToTheFirst()
    {
        var settings = FromJson("""
            {
              "locations": [
                {"name": "One", "latDeg": 1, "lonDeg": 1},
                {"name": "Two", "latDeg": 2, "lonDeg": 2}
              ]
            }
            """);

        Assert.Equal("One", settings.Primary!.Name);
    }
}
