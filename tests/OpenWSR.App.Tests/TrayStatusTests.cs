using OpenWSR.App;

namespace OpenWSR.App.Tests;

/// <summary>
/// The tray line is the entire user interface while OpenWSR is hidden, so what it says has to
/// survive being the only thing said. Two properties matter: a threat always wins over the
/// watch list, and the tooltip never exceeds what the shell will take — WinForms throws above
/// 63 characters rather than truncating, so an over-long place name would take the tray icon
/// down with it and the alerting with that.
/// </summary>
public class TrayStatusTests
{
    private static WatchedPlace Place(string name = "Home") => new(name, 35.0, -97.0, 40);

    private static Threat Approaching(string label = "Storm A1", double distanceKm = 20) =>
        new("storm:A1", $"{label} is heading for you", label, "detail",
            ThreatRank.Direct, distanceKm, EtaMinutes: 19, LatDeg: 35.2, LonDeg: -97.2);

    [Fact]
    public void SaysSoWhenNowhereIsBeingWatched()
    {
        // The failure this guards against is silence that looks like success: an app sitting
        // in the tray with no place saved is not watching anything, and looks exactly like
        // one that is watching and has nothing to report.
        string text = TrayStatus.Describe([], []);

        Assert.Contains("Not watching", text);
    }

    [Fact]
    public void NamesTheOnePlaceItIsWatching()
    {
        string text = TrayStatus.Describe([Place("Norman")], []);

        Assert.Contains("Norman", text);
        Assert.Contains("nothing approaching", text);
    }

    [Fact]
    public void CountsPlacesRatherThanListingThemAll()
    {
        string text = TrayStatus.Describe(
            [Place("Home"), Place("Office"), Place("Mum")], []);

        Assert.Contains("3 places", text);
        Assert.DoesNotContain("Office", text);
    }

    [Fact]
    public void AThreatOutranksTheWatchList()
    {
        var threat = Approaching();

        string text = TrayStatus.Describe([Place()], [threat]);

        Assert.StartsWith(threat.Label, text);
        Assert.Contains(threat.Range, text);
        Assert.DoesNotContain("Watching", text);
    }

    [Fact]
    public void CountsTheRestRatherThanNamingThem()
    {
        var first = Approaching("Storm A1");
        string text = TrayStatus.Describe(
            [Place()], [first, Approaching("Storm B2"), Approaching("Storm C3")]);

        Assert.StartsWith(first.Label, text);
        Assert.Contains("+2 more", text);
        Assert.DoesNotContain("Storm B2", text);
    }

    [Fact]
    public void TooltipNamesTheApp()
    {
        Assert.StartsWith("OpenWSR", TrayStatus.Tooltip([Place()], []));
    }

    [Fact]
    public void TooltipStaysInsideTheShellLimit()
    {
        // A place named at length is the realistic way to overrun it — the name is the user's
        // to choose, and nothing else in this string is.
        var absurd = new WatchedPlace(new string('x', 200), 35, -97, 40);

        string tooltip = TrayStatus.Tooltip([absurd], []);

        Assert.True(
            tooltip.Length <= TrayStatus.TooltipLimit,
            $"tooltip was {tooltip.Length} characters: {tooltip}");
        Assert.EndsWith("…", tooltip);
    }

    [Fact]
    public void TooltipLeavesAShortLineAlone()
    {
        string tooltip = TrayStatus.Tooltip([Place("Home")], []);

        Assert.DoesNotContain("…", tooltip);
    }
}
