using System.IO;
using OpenWSR.App;

namespace OpenWSR.App.Tests;

/// <summary>
/// The archive loop's span used to be a compile-time constant. It is a real cost — every
/// frame is a downloaded, decoded sweep held in memory — so it is now a setting, and these
/// pin the bounds a hand-edited settings file has to survive.
/// </summary>
public sealed class LoopLengthTests
{
    [Fact]
    public void ThirtyVolumesRemainsTheDefault() =>
        Assert.Equal(ArchivePlaybackController.DefaultLoopFrames, new AppSettings().LoopFrames);

    [Theory]
    [InlineData(0, 2)]        // a loop of nothing
    [InlineData(1, 2)]        // one frame is not a loop
    [InlineData(-40, 2)]
    [InlineData(12, 12)]
    [InlineData(30, 30)]
    [InlineData(144, 144)]
    [InlineData(5000, 144)]   // a whole day of volumes is over a gigabyte of geometry
    public void AnOutOfRangeSpanIsClampedRatherThanHonoured(int asked, int expected) =>
        Assert.Equal(expected, ArchivePlaybackController.ClampLoopFrames(asked));

    [Fact]
    public void TheCeilingIsTwelveHoursOfAFiveMinuteVolumeCoverage()
    {
        // 144 x 5 minutes. Stated as a duration because that is what the setting means to
        // someone choosing it, not as a count that happens to be round.
        Assert.Equal(12.0, ArchivePlaybackController.MaxLoopFrames * 5 / 60.0, 3);
        Assert.True(ArchivePlaybackController.DefaultLoopFrames < ArchivePlaybackController.MaxLoopFrames);
    }

    [Fact]
    public void TheSpanSurvivesASettingsRoundTrip()
    {
        // Written through the real serializer so a rename of the JSON property is caught.
        var settings = new AppSettings { LoopFrames = 60 };
        string json = System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });

        Assert.Contains("\"loopFrames\": 60".Replace(": ", ":"), json.Replace(": ", ":"));

        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
        Assert.Equal(60, restored!.LoopFrames);
    }

    [Fact]
    public void ASettingsFileWithNoLoopFramesStillLoads()
    {
        // Every settings file written before this setting existed.
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{"tileProvider":"osm","units":"Imperial"}""",
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });

        Assert.Equal(ArchivePlaybackController.DefaultLoopFrames, restored!.LoopFrames);
    }
}
