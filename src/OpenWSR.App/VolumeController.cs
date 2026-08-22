using OpenWSR.Nexrad.Analysis;
using OpenWSR.Render;
using OpenWSR.Render.Radar;

namespace OpenWSR.App;

/// <summary>
/// The 3D view: turns the volume currently on screen into a Cartesian grid and hands it to
/// the renderer.
///
/// Resampling is not free — a few hundred milliseconds for a full volume — so it happens
/// off the UI thread and only when the 3D view is actually open. Leaving 3D drops the grid
/// rather than keeping it warm: it is tens of megabytes, and the next volume would make it
/// stale anyway.
/// </summary>
public sealed class VolumeController(MapView mapView, RadarDisplayController radar)
{
    /// <summary>
    /// 256 across and 64 up. The horizontal cell is then about 1.2 km at the default range,
    /// which is close to the beam width at that distance — a finer grid would be inventing
    /// detail the radar did not resolve. 4 MB of voxels, which is nothing for a texture.
    /// </summary>
    private const int HorizontalCells = 256;
    private const int VerticalCells = 64;

    private const double DefaultHalfWidthM = 150_000;
    /// <summary>
    /// Top of the resampled box. Public because the camera has to frame the same height the
    /// grid was built to — two copies of the number drift apart silently.
    /// </summary>
    public const double TopHeightM = 20_000;

    private CancellationTokenSource? _building;
    private CancellationTokenSource? _debounce;
    private bool _hasVolumeOnScreen;

    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorRaised;

    /// <summary>Raised when a build finishes, so the shell can re-enable its controls.</summary>
    public event Action? Changed;

    public bool IsActive { get; private set; }

    /// <summary>How far out the box reaches. A tighter box is a finer grid over one storm.</summary>
    public double HalfWidthM { get; set; } = DefaultHalfWidthM;

    /// <summary>Description of what is loaded, for the status line.</summary>
    public string? Summary { get; private set; }

    /// <summary>
    /// Coalesce rebuild requests. A live volume arrives one cut at a time, so the display
    /// controller announces a change fifteen times over five minutes — and resampling
    /// fifteen times to throw away fourteen of the results is most of a CPU core spent on
    /// nothing. Explicit user actions call <see cref="RebuildAsync"/> and are not delayed.
    /// </summary>
    public async Task RequestRebuildAsync()
    {
        if (!IsActive) return;

        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = _debounce = new CancellationTokenSource();

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // a newer request took over
        }

        await RebuildAsync();
    }

    public async Task EnterAsync()
    {
        IsActive = true;
        mapView.VolumeMode = true;
        await RebuildAsync();
    }

    public void Leave()
    {
        IsActive = false;
        _hasVolumeOnScreen = false;
        _debounce?.Cancel();
        _building?.Cancel();
        mapView.VolumeMode = false;
        mapView.ClearVolume();
        Summary = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Resample the volume on screen. Safe to call on every product change or new volume —
    /// it cancels a build already running rather than queueing behind it, because the
    /// result of that one is about to be thrown away.
    /// </summary>
    public async Task RebuildAsync()
    {
        if (!IsActive) return;

        _building?.Cancel();
        _building?.Dispose();
        var cts = _building = new CancellationTokenSource();
        var token = cts.Token;

        var table = radar.CurrentTable;
        var palette = table.BuildRgba256();
        float min = table.MinValue, max = table.MaxValue;
        double halfWidth = HalfWidthM;

        try
        {
            // Materialising the cuts is not the cheap part it looks like. For a derived
            // moment it dealiases and recomputes every one of them — fourteen dealiases for
            // a VCP 212 volume — so it belongs behind the same Task.Run as the grid build.
            // Left on the UI thread it froze the window each time a live volume arrived.
            var sweeps = await Task.Run(radar.SweepsForCurrentMoment, token);

            if (token.IsCancellationRequested || !IsActive) return;

            // A live volume is published as it is scanned, so for the first minutes of every
            // scan there are too few cuts to build from. Keep showing the volume already up
            // rather than blanking the view every five minutes — the old one is a few minutes
            // stale, which is far better than nothing at all.
            if (sweeps.Count < 2)
            {
                string why = sweeps.Count == 0
                    ? "No volume loaded to build a 3D view from."
                    : "3D needs more than one elevation cut; this volume is still scanning.";
                StatusChanged?.Invoke(_hasVolumeOnScreen ? $"{why} Showing the previous volume." : why);

                if (!_hasVolumeOnScreen)
                {
                    mapView.ClearVolume();
                    Summary = null;
                }
                return;
            }

            StatusChanged?.Invoke($"Building the 3D volume from {sweeps.Count} cuts…");

            var grid = await Task.Run(() => VolumeGrid3D.Build(
                sweeps, min, max,
                horizontalCells: HorizontalCells,
                verticalCells: VerticalCells,
                halfWidthM: halfWidth,
                topHeightM: TopHeightM), token);

            if (token.IsCancellationRequested || !IsActive) return;

            _hasVolumeOnScreen = true;
            mapView.SetVolume(new VolumeUpload(
                grid.Voxels, grid.Nx, grid.Ny, grid.Nz,
                grid.HalfWidthM, grid.BaseHeightM, grid.TopHeightM,
                palette, min, max, table.Name));

            double filled = grid.FilledCount / (double)grid.Voxels.Length;
            Summary =
                $"{grid.ElevationsUsed.Count} cuts, "
                + $"{grid.ElevationsUsed[0]:F1}°–{grid.ElevationsUsed[^1]:F1}°, "
                + $"{halfWidth / 1000:F0} km across, {TopHeightM / 1000:F0} km up "
                + $"· {filled:P0} of the box sampled";
            StatusChanged?.Invoke($"3D: {Summary}");
        }
        catch (OperationCanceledException)
        {
            // A newer build replaced this one; its result is the one that matters.
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke($"Could not build the 3D volume: {ex.Message}");
        }
        finally
        {
            Changed?.Invoke();
        }
    }
}
