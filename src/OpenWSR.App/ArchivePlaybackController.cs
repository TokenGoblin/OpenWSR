using System.IO;
using System.Windows.Threading;
using OpenWSR.Ingest;
using OpenWSR.Nexrad;
using OpenWSR.Render;
using OpenWSR.Render.Radar;

namespace OpenWSR.App;

/// <summary>
/// Archive browsing and loop playback: lists a site's volumes for a UTC day, scrubs to a
/// volume, and pre-builds loop frames (prebuilt sweep geometry, cycled with zero per-frame
/// CPU geometry work) for the last N volumes.
/// </summary>
public sealed class ArchivePlaybackController(
    MapView mapView, RadarDisplayController radar) : IDisposable
{
    public const int LoopFrames = 30;

    private readonly ArchiveClient _archive = new();
    private readonly DispatcherTimer _loopTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<ArchiveVolumeRef> _dayVolumes = [];
    private CancellationTokenSource? _scrubCts;
    private List<(DateTime Time, SweepGeometry Geometry)>? _loop;
    private byte[]? _loopPalette;
    private float _loopPaletteMin, _loopPaletteRange;
    private int _loopPosition;
    private bool _initialized;

    public event Action<string>? StatusChanged;
    public event Action<int, int>? DayLoaded;      // volume count, initial index
    public event Action<bool>? PlayingChanged;

    public bool IsPlaying { get; private set; }
    public IReadOnlyList<ArchiveVolumeRef> DayVolumes => _dayVolumes;

    public void SetSpeed(double framesPerSecond) =>
        _loopTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / framesPerSecond);

    public async Task LoadDayAsync(string siteId, DateOnly dateUtc)
    {
        StopLoop();
        StatusChanged?.Invoke($"Listing {siteId} {dateUtc:yyyy-MM-dd}…");
        try
        {
            _dayVolumes = await _archive.ListVolumesAsync(siteId, dateUtc);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"List failed: {ex.Message}");
            return;
        }
        if (_dayVolumes.Count == 0)
        {
            StatusChanged?.Invoke($"No volumes for {siteId} on {dateUtc:yyyy-MM-dd}.");
            return;
        }
        DayLoaded?.Invoke(_dayVolumes.Count, _dayVolumes.Count - 1);
        await ScrubToAsync(_dayVolumes.Count - 1);
    }

    /// <summary>Show the volume at a slider index; superseded scrubs are cancelled.</summary>
    public async Task ScrubToAsync(int index)
    {
        if (index < 0 || index >= _dayVolumes.Count) return;
        StopLoop();
        _scrubCts?.Cancel();
        var cts = _scrubCts = new CancellationTokenSource();
        var volumeRef = _dayVolumes[index];
        StatusChanged?.Invoke($"Loading {Path.GetFileName(volumeRef.Key)}…");
        try
        {
            var volume = await Task.Run(async () =>
            {
                var path = await _archive.FetchVolumeAsync(volumeRef, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                return ArchiveFile.DecodeFile(path);
            }, cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            if (!_initialized)
            {
                _initialized = true;
                mapView.Camera.MoveTo(volume.LatDeg, volume.LonDeg, 250);
            }
            radar.ShowVolume(volume);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Load failed: {ex.Message}");
        }
    }

    /// <summary>Build the loop (last N volumes, current product selection) and start playing.</summary>
    public async Task PlayAsync()
    {
        if (IsPlaying || _dayVolumes.Count == 0) return;
        StopLoop();
        IsPlaying = true;
        PlayingChanged?.Invoke(true);

        var window = _dayVolumes.TakeLast(LoopFrames).ToList();
        var table = radar.CurrentTable;
        _loopPalette = table.BuildRgba256();
        _loopPaletteMin = table.MinValue;
        _loopPaletteRange = table.Range;

        var frames = new List<(DateTime, SweepGeometry)>(window.Count);
        try
        {
            // Prefetch in parallel (bounded), then decode sequentially for steady progress.
            var fetches = new SemaphoreSlim(3);
            int fetched = 0;
            var paths = await Task.WhenAll(window.Select(async v =>
            {
                await fetches.WaitAsync();
                try
                {
                    var path = await _archive.FetchVolumeAsync(v);
                    StatusChanged?.Invoke($"Downloading loop volumes… {Interlocked.Increment(ref fetched)}/{window.Count}");
                    return path;
                }
                finally { fetches.Release(); }
            }));

            for (int i = 0; i < window.Count; i++)
            {
                if (!IsPlaying) return; // user pressed stop during the build
                StatusChanged?.Invoke($"Building loop {i + 1}/{window.Count}…");
                int idx = i;
                var geometry = await Task.Run(() =>
                {
                    var volume = ArchiveFile.DecodeFile(paths[idx]);
                    var sweep = radar.SelectSweep(volume);
                    return sweep is null ? null : SweepGeometry.Build(sweep);
                });
                if (geometry is not null)
                    frames.Add((window[i].TimeUtc, geometry));
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Loop build failed: {ex.Message}");
            StopLoop();
            return;
        }

        if (frames.Count == 0 || !IsPlaying)
        {
            StopLoop();
            return;
        }
        _loop = frames;
        _loopPosition = 0;
        _loopTimer.Tick -= OnLoopTick;
        _loopTimer.Tick += OnLoopTick;
        _loopTimer.Start();
    }

    private void OnLoopTick(object? sender, EventArgs e)
    {
        if (_loop is not { Count: > 0 } frames) return;
        var (time, geometry) = frames[_loopPosition];
        mapView.ShowGeometry(geometry, _loopPalette!, _loopPaletteMin, _loopPaletteRange);
        StatusChanged?.Invoke(
            $"Loop {_loopPosition + 1}/{frames.Count}  {time:yyyy-MM-dd HH:mm:ss}Z  {radar.CurrentMoment}");
        _loopPosition = (_loopPosition + 1) % frames.Count;
    }

    public void StopLoop()
    {
        _loopTimer.Stop();
        _loopTimer.Tick -= OnLoopTick;
        _loop = null;
        if (IsPlaying)
        {
            IsPlaying = false;
            PlayingChanged?.Invoke(false);
        }
    }

    public void Dispose()
    {
        StopLoop();
        _scrubCts?.Cancel();
        _archive.Dispose();
    }
}
