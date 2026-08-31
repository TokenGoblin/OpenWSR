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
    MapView mapView, RadarDisplayController radar) : IDisposable, ITimedLayer
{
    /// <summary>Two and a half hours of a five-minute VCP — a storm's life, without a long wait.</summary>
    public const int DefaultLoopFrames = 30;

    /// <summary>
    /// A ceiling rather than a judgement about what is useful. A full UTC day is around 250
    /// volumes; at roughly 5 MB of held geometry each that is over a gigabyte, and the
    /// download alone would run long enough that the loop is no longer about the weather you
    /// were watching. Twelve hours is as far as this goes.
    /// </summary>
    public const int MaxLoopFrames = 144;

    private int _loopFrames = DefaultLoopFrames;

    /// <summary>
    /// How many of the day's most recent volumes the loop spans. Clamped on the way in, so
    /// a hand-edited settings file cannot ask for a loop that will not fit.
    /// </summary>
    public int LoopFrames
    {
        get => _loopFrames;
        set => _loopFrames = ClampLoopFrames(value);
    }

    /// <summary>
    /// The clamp, reachable without a render device so it can be tested. Two frames is the
    /// floor because one frame is not a loop.
    /// </summary>
    public static int ClampLoopFrames(int frames) => Math.Clamp(frames, 2, MaxLoopFrames);

    private readonly ArchiveClient _archive = new();
    private readonly DispatcherTimer _loopTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<ArchiveVolumeRef> _dayVolumes = [];
    private CancellationTokenSource? _scrubCts;
    private CancellationTokenSource? _buildCts;
    private List<(DateTime Time, SweepGeometry Geometry)>? _loop;
    private byte[]? _loopPalette;
    private float _loopPaletteMin, _loopPaletteRange;
    private string? _loopSignature;
    private int _loopPosition;
    private bool _initialized;

    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorRaised;
    public event Action<double>? ProgressChanged;   // 0..1, negative clears the indicator
    public event Action<int, int>? DayLoaded;       // volume count, initial index
    public event Action<bool>? PlayingChanged;
    public event Action<int>? LoopFrameShown;       // index of the frame now on screen
    public event Action<RadarVolume>? VolumeLoaded; // scrub target decoded (routed to all panes)

    public bool IsPlaying { get; private set; }
    public IReadOnlyList<ArchiveVolumeRef> DayVolumes => _dayVolumes;

    /// <summary>Frames currently prepared for looping; zero when no loop has been built.</summary>
    public int LoopGeometryCount => _loop?.Count ?? 0;

    /// <summary>True when a loop could be built — i.e. a day is loaded.</summary>
    public bool CanBuildLoop => _dayVolumes.Count > 0;

    /// <summary>Index into <see cref="DayVolumes"/> of the first frame in the loop window.</summary>
    public int LoopWindowStart => Math.Max(0, _dayVolumes.Count - _loopFrames);

    /// <summary>Show one loop frame without playing — used when recording a GIF.</summary>
    public void ShowLoopFrame(int index)
    {
        if (_loop is not { Count: > 0 } frames) return;
        index = Math.Clamp(index, 0, frames.Count - 1);
        var (time, geometry) = frames[index];
        mapView.ShowGeometry(geometry, _loopPalette!, _loopPaletteMin, _loopPaletteRange);
        _loopPosition = index;
        LoopFrameShown?.Invoke(index);
        StatusChanged?.Invoke($"Frame {index + 1}/{frames.Count}  {time:HH:mm:ss}Z");
    }

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
            ErrorRaised?.Invoke($"Could not list {siteId} for {dateUtc:yyyy-MM-dd}: {ex.Message}");
            return;
        }
        if (_dayVolumes.Count == 0)
        {
            StatusChanged?.Invoke($"No volumes for {siteId} on {dateUtc:yyyy-MM-dd}.");
            DayLoaded?.Invoke(0, 0);
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
        _scrubCts?.Dispose();
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
            VolumeLoaded?.Invoke(volume);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke($"Could not load {Path.GetFileName(volumeRef.Key)}: {ex.Message}");
        }
    }

    /// <summary>
    /// What the current loop would have to match to be reusable: the day's volume set, the
    /// product it was built for, and how far back it reaches. Rebuilding a loop is minutes
    /// of work, so a pause-and-resume (or a GIF recording) must not trigger one.
    /// </summary>
    private string LoopSignature() =>
        $"{radar.CurrentMoment}|{_loopFrames}|{_dayVolumes.Count}|"
        + $"{(_dayVolumes.Count > 0 ? _dayVolumes[^1].Key : "")}";

    /// <summary>
    /// Build the loop frames for the current day and product unless usable frames already
    /// exist. Returns false when there is nothing to build or the build failed.
    /// </summary>
    public async Task<bool> EnsureLoopAsync()
    {
        if (_dayVolumes.Count == 0) return false;
        if (_loop is { Count: > 0 } && _loopSignature == LoopSignature()) return true;

        _buildCts?.Cancel();
        _buildCts?.Dispose();
        var cts = _buildCts = new CancellationTokenSource();

        var window = _dayVolumes.TakeLast(_loopFrames).ToList();
        var table = radar.CurrentTable;
        var palette = table.BuildRgba256();

        var frames = new List<(DateTime, SweepGeometry)>(window.Count);
        try
        {
            // Prefetch in parallel (bounded), then decode sequentially for steady progress.
            var fetches = new SemaphoreSlim(3);
            int fetched = 0;
            var paths = await Task.WhenAll(window.Select(async v =>
            {
                await fetches.WaitAsync(cts.Token);
                try
                {
                    var path = await _archive.FetchVolumeAsync(v, cts.Token);
                    int done = Interlocked.Increment(ref fetched);
                    StatusChanged?.Invoke($"Downloading loop volumes… {done}/{window.Count}");
                    ProgressChanged?.Invoke(0.5 * done / window.Count);
                    return path;
                }
                finally { fetches.Release(); }
            }));

            for (int i = 0; i < window.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                StatusChanged?.Invoke($"Building loop {i + 1}/{window.Count}…");
                ProgressChanged?.Invoke(0.5 + 0.5 * (i + 1) / window.Count);
                int idx = i;
                var geometry = await Task.Run(() =>
                {
                    var volume = ArchiveFile.DecodeFile(paths[idx]);
                    var sweep = radar.SelectSweep(volume);
                    return sweep is null ? null : SweepGeometry.Build(sweep);
                }, cts.Token);
                if (geometry is not null)
                    frames.Add((window[i].TimeUtc, geometry));
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke($"Could not build the loop: {ex.Message}");
            return false;
        }
        finally
        {
            ProgressChanged?.Invoke(-1);
        }

        if (frames.Count == 0)
        {
            StatusChanged?.Invoke($"No {radar.CurrentMoment} data in this day's volumes.");
            return false;
        }

        _loop = frames;
        _loopPalette = palette;
        _loopPaletteMin = table.MinValue;
        _loopPaletteRange = table.Range;
        _loopSignature = LoopSignature();
        _loopPosition = 0;
        return true;
    }

    /// <summary>Build the loop if needed, then start cycling it.</summary>
    public async Task PlayAsync()
    {
        if (IsPlaying || _dayVolumes.Count == 0) return;
        if (!await EnsureLoopAsync()) return;

        IsPlaying = true;
        PlayingChanged?.Invoke(true);
        _loopTimer.Tick -= OnLoopTick;
        _loopTimer.Tick += OnLoopTick;
        _loopTimer.Start();
    }

    private void OnLoopTick(object? sender, EventArgs e)
    {
        if (_loop is not { Count: > 0 } frames) return;
        var (time, geometry) = frames[_loopPosition];
        mapView.ShowGeometry(geometry, _loopPalette!, _loopPaletteMin, _loopPaletteRange);
        LoopFrameShown?.Invoke(_loopPosition);
        StatusChanged?.Invoke(
            $"Loop {_loopPosition + 1}/{frames.Count}  {time:yyyy-MM-dd HH:mm:ss}Z  {radar.CurrentMoment}");
        _loopPosition = (_loopPosition + 1) % frames.Count;
    }

    /// <summary>
    /// Stop cycling but keep the built frames. The GIF recorder steps them by hand and
    /// resuming must not re-download the whole window.
    /// </summary>
    public void PauseLoop()
    {
        _loopTimer.Stop();
        _loopTimer.Tick -= OnLoopTick;
        if (IsPlaying)
        {
            IsPlaying = false;
            PlayingChanged?.Invoke(false);
        }
    }

    private bool _pollingSuspended;

    /// <summary>
    /// See <see cref="ITimedLayer"/>. The loop stages a texture per frame, up to ten times a
    /// second, so it is the most wasteful clock in the app to leave running against a paused
    /// renderer. <see cref="PauseLoop"/> keeps the built frames, so resuming is free.
    /// </summary>
    public void SuspendPolling()
    {
        // PauseLoop clears IsPlaying, so whether to come back playing has to be recorded here.
        _pollingSuspended = IsPlaying;
        if (IsPlaying) PauseLoop();
    }

    /// <inheritdoc />
    public void ResumePolling()
    {
        if (!_pollingSuspended) return;
        _pollingSuspended = false;
        _ = PlayAsync();
    }

    /// <summary>Stop cycling and discard the frames — the selection they were built for is gone.</summary>
    public void StopLoop()
    {
        PauseLoop();
        _buildCts?.Cancel();
        _loop = null;
        _loopSignature = null;
    }

    public void Dispose()
    {
        StopLoop();
        _buildCts?.Dispose();
        _scrubCts?.Cancel();
        _scrubCts?.Dispose();
        _archive.Dispose();
    }
}
