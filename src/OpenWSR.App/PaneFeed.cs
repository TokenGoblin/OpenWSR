using OpenWSR.Ingest;
using OpenWSR.Nexrad;

namespace OpenWSR.App;

/// <summary>
/// Data for one pane that is showing a site of its own.
///
/// A pane normally mirrors whatever the primary is showing, which is what makes the
/// multi-pane view a product comparison. Pinning it to a site turns it into a place
/// comparison instead — two storms, two radars, side by side — and that needs its own
/// feed, because nothing else in the app is looking at that site.
///
/// Live and archive are both supported deliberately. A pinned pane that quietly went
/// blank the moment you scrubbed back would be worse than not offering the option.
/// </summary>
public sealed class PaneFeed : IDisposable
{
    private readonly ArchiveClient _archive = new();
    private LiveFeed? _live;

    private string? _site;
    private DateOnly _listedDay;
    private string? _listedSite;
    private IReadOnlyList<ArchiveVolumeRef> _day = [];
    private string? _shownKey;
    private CancellationTokenSource? _cts;

    /// <summary>Raised with a decoded volume for this pane. Marshalled by the caller.</summary>
    public event Action<RadarVolume>? VolumeReady;

    public event Action<string>? StatusChanged;

    /// <summary>The site this pane is pinned to, or null when it follows the primary.</summary>
    public string? Site => _site;

    public bool IsPinned => _site is not null;

    /// <summary>
    /// Point the pane at a site, or back at the primary. Returns once the feed has been
    /// torn down or stood up; the data itself arrives through <see cref="VolumeReady"/>.
    /// </summary>
    public async Task SetSiteAsync(string? site)
    {
        if (string.Equals(_site, site, StringComparison.OrdinalIgnoreCase)) return;
        _site = site;
        _shownKey = null;

        if (_live is not null)
        {
            await _live.StopAsync();
            _live.Dispose();
            _live = null;
        }
    }

    /// <summary>
    /// Bring the pane in line with what the rest of the app is showing. Called whenever
    /// the mode, the day, or the scrub position changes; a no-op for an unpinned pane.
    /// </summary>
    public async Task SyncAsync(DataMode mode, DateOnly day, DateTime targetUtc)
    {
        if (_site is not { } site) return;

        if (mode == DataMode.Live)
        {
            if (_live is null)
            {
                _live = new LiveFeed();
                _live.VolumeUpdated += (volume, _) => VolumeReady?.Invoke(volume);
                await _live.StartAsync(site);
            }
            return;
        }

        // Leaving live: stop streaming before pulling from the archive, or the pane
        // flickers between the two.
        if (_live is not null)
        {
            await _live.StopAsync();
            _live.Dispose();
            _live = null;
        }
        if (mode != DataMode.Archive) return;

        await ShowArchiveNearestAsync(site, day, targetUtc);
    }

    /// <summary>
    /// Show this site's volume closest in time to what the primary is showing. Scans are
    /// not synchronised between radars, so "the same moment" is the nearest scan rather
    /// than an exact match.
    /// </summary>
    private async Task ShowArchiveNearestAsync(string site, DateOnly day, DateTime targetUtc)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();

        try
        {
            if (_listedSite != site || _listedDay != day)
            {
                _day = await _archive.ListVolumesAsync(site, day);
                _listedSite = site;
                _listedDay = day;
            }
            if (_day.Count == 0)
            {
                StatusChanged?.Invoke($"{site}: nothing archived for {day:yyyy-MM-dd}.");
                return;
            }

            var nearest = _day.MinBy(v => Math.Abs((v.TimeUtc - targetUtc).Ticks))!;
            if (nearest.Key == _shownKey) return; // already on screen

            var path = await _archive.FetchVolumeAsync(nearest, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var volume = await Task.Run(() => ArchiveFile.DecodeFile(path), cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            _shownKey = nearest.Key;
            VolumeReady?.Invoke(volume);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"{site}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _live?.Dispose();
        _archive.Dispose();
    }
}
