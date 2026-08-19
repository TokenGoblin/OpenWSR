using OpenWSR.App;
using OpenWSR.Nexrad;

namespace OpenWSR.App.Tests;

/// <summary>
/// The product bar renders straight off this state, so "which buttons are lit and which
/// are greyed" is a testable question rather than something only a screenshot can answer.
/// </summary>
public class MainViewModelTests
{
    [Fact]
    public void EverySupportedMomentHasAButtonWithItsShortcut()
    {
        var vm = new MainViewModel();

        Assert.Equal(6, vm.Moments.Count);
        Assert.Equal(["REF", "VEL", "SW", "ZDR", "PHI", "CC"], vm.Moments.Select(m => m.Label));
        Assert.Equal(["R", "V", "W", "D", "P", "C"], vm.Moments.Select(m => m.Key));
        Assert.All(vm.Moments, m => Assert.False(string.IsNullOrWhiteSpace(m.Description)));
    }

    [Fact]
    public void MomentsAbsentFromTheVolumeAreUnavailable()
    {
        var vm = new MainViewModel();

        // A legacy volume with no dual-pol moments.
        vm.SyncMoments([Moment.Reflectivity, Moment.Velocity], Moment.Reflectivity);

        Assert.True(vm.Moments.Single(m => m.Moment == Moment.Reflectivity).IsAvailable);
        Assert.True(vm.Moments.Single(m => m.Moment == Moment.Velocity).IsAvailable);
        Assert.False(vm.Moments.Single(m => m.Moment == Moment.CorrelationCoefficient).IsAvailable);
    }

    [Fact]
    public void ExactlyOneMomentIsEverSelected()
    {
        var vm = new MainViewModel();
        vm.SyncMoments([Moment.Reflectivity, Moment.Velocity], Moment.Velocity);
        Assert.Single(vm.Moments, m => m.IsSelected);
        Assert.Equal(Moment.Velocity, vm.Moments.Single(m => m.IsSelected).Moment);

        vm.SyncMoments([Moment.Reflectivity, Moment.Velocity], Moment.Reflectivity);
        Assert.Single(vm.Moments, m => m.IsSelected);
        Assert.Equal(Moment.Reflectivity, vm.Moments.Single(m => m.IsSelected).Moment);
    }

    [Fact]
    public void UnavailableMomentTooltipExplainsWhyItIsGreyed()
    {
        var vm = new MainViewModel();
        vm.SyncMoments([Moment.Reflectivity], Moment.Reflectivity);

        var cc = vm.Moments.Single(m => m.Moment == Moment.CorrelationCoefficient);
        Assert.Contains("not in this volume", cc.Tooltip);

        var refl = vm.Moments.Single(m => m.Moment == Moment.Reflectivity);
        Assert.Contains("(R)", refl.Tooltip);
    }

    [Fact]
    public void TiltsAreLabelledByAngleAndSelectedByPosition()
    {
        var vm = new MainViewModel();
        vm.SyncTilts([0.4834f, 1.5f, 2.4f], position: 1);

        Assert.Equal(3, vm.Tilts.Count);
        // Binary-angle-unit decoding gives 0.4834° for the nominal 0.5° cut; the label
        // rounds for display but the list is built from the decoded value.
        Assert.Equal("0.5°", vm.Tilts[0].Label);
        Assert.Equal("1.5°", vm.Tilts[1].Label);
        Assert.Equal(vm.Tilts[1], vm.SelectedTilt);
        Assert.Equal("1.5°", vm.SelectedTilt!.ToString());
    }

    [Fact]
    public void SyncingTiltsDoesNotEchoBackAsAUserSelection()
    {
        var vm = new MainViewModel();
        // The flag must be clear once the sync completes, or the combo stops responding.
        vm.SyncTilts([0.5f, 1.5f], position: 0);
        Assert.False(vm.SuppressTiltEcho);
    }

    [Fact]
    public void AnEmptyVolumeLeavesNoTiltSelected()
    {
        var vm = new MainViewModel();
        vm.SyncTilts([0.5f], position: 0);
        Assert.NotNull(vm.SelectedTilt);

        vm.SyncTilts([], position: 0);
        Assert.Empty(vm.Tilts);
        Assert.Null(vm.SelectedTilt);
    }

    [Fact]
    public void OutOfRangePositionDoesNotThrow()
    {
        var vm = new MainViewModel();
        vm.SyncTilts([0.5f, 1.5f], position: 99);
        Assert.Null(vm.SelectedTilt);
    }

    [Fact]
    public void ModeIsMutuallyExclusive()
    {
        var vm = new MainViewModel { Mode = DataMode.Live };
        Assert.True(vm.IsLive);
        Assert.False(vm.IsArchive);
        Assert.False(vm.IsForecast);

        vm.Mode = DataMode.Forecast;
        Assert.False(vm.IsLive);
        Assert.True(vm.IsForecast);
    }

    [Fact]
    public void ChangingModeNotifiesTheDerivedFlags()
    {
        var vm = new MainViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.Mode = DataMode.Live;

        // The three transports bind to these, so they must all be raised.
        Assert.Contains(nameof(MainViewModel.IsLive), changed);
        Assert.Contains(nameof(MainViewModel.IsArchive), changed);
        Assert.Contains(nameof(MainViewModel.IsForecast), changed);
    }

    [Theory]
    [InlineData(MapTool.Measure, "measure")]
    [InlineData(MapTool.CrossSection, "slice")]
    [InlineData(MapTool.SetHome, "home")]
    [InlineData(MapTool.Inspect, "Hover")]
    public void EveryToolExplainsItsGesture(MapTool tool, string fragment) =>
        Assert.Contains(fragment, MainViewModel.ToolHint(tool), StringComparison.OrdinalIgnoreCase);
}
