using OpenWSR.Ingest;
using OpenWSR.Nexrad;
using OpenWSR.Nexrad.Analysis;
using OpenWSR.Palettes;
using OpenWSR.Render;

namespace OpenWSR.App;

/// <summary>
/// Builds a rotation-track swath from a run of archive volumes and puts it on the map.
///
/// A single scan says where rotation is now; a track says where it has been. That is the
/// question a damage survey asks, and the one that distinguishes a couplet that held
/// together for half an hour from one that fell apart in five minutes.
/// </summary>
public sealed class RotationTracksController(MapView mapView) : IDisposable
{
    /// <summary>
    /// Scans to accumulate. Twelve volumes is roughly an hour of a severe-weather VCP —
    /// long enough for a track to be a track, short enough that building it is tens of
    /// seconds rather than minutes.
    /// </summary>
    public const int DefaultScanCount = 12;

    /// <summary>
    /// Below this the swath is drawn transparent. Ordinary shear is everywhere; painting
    /// all of it buries the tracks in a wash of colour. This sits at the palette's own
    /// onset of "rotation", so what shows is what the live product would also call
    /// rotation.
    /// </summary>
    private const float VisibleThreshold = 0.006f;

    private readonly ArchiveClient _archive = new();
    private CancellationTokenSource? _cts;
    private RotationTrackResult? _result;
    private float _opacity = 0.85f;

    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorRaised;
    public event Action<double>? ProgressChanged;   // 0..1; negative clears

    public bool IsLoaded => _result is not null;

    /// <summary>A one-line summary of what is on screen, or null when nothing is.</summary>
    public string? Summary => _result is not { } r
        ? null
        : $"{r.ScanCount} scans, {r.StartUtc:HH:mm}–{r.EndUtc:HH:mm}Z · peak {r.Peak:F4} 1/s";

    public float Opacity
    {
        get => _opacity;
        set
        {
            _opacity = Math.Clamp(value, 0f, 1f);
            if (_result is not null) Show(_result);
        }
    }

    /// <summary>
    /// Accumulate the scans ending at <paramref name="endIndex"/> in a loaded archive day.
    /// It ends where the timeline is rather than at midnight: the useful swath is the hour
    /// leading up to whatever you are looking at, not the last hour of the day.
    /// Decoding happens on the thread pool; only the finished raster crosses back.
    /// </summary>
    public async Task BuildAsync(
        IReadOnlyList<ArchiveVolumeRef> dayVolumes, int endIndex, int scanCount = DefaultScanCount)
    {
        if (dayVolumes.Count == 0)
        {
            ErrorRaised?.Invoke("Load an archive day before building rotation tracks.");
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();

        int end = Math.Clamp(endIndex, 0, dayVolumes.Count - 1);
        int start = Math.Max(0, end - scanCount + 1);
        var window = dayVolumes.Skip(start).Take(end - start + 1).ToList();

        try
        {
            var shear = new List<Sweep>(window.Count);
            for (int i = 0; i < window.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                StatusChanged?.Invoke($"Rotation tracks: scan {i + 1} of {window.Count}…");
                ProgressChanged?.Invoke((double)i / window.Count);

                var path = await _archive.FetchVolumeAsync(window[i], cts.Token);
                var sweep = await Task.Run(() =>
                {
                    var volume = ArchiveFile.DecodeFile(path);
                    // The lowest Doppler cut: rotation tracks are a near-ground product,
                    // and it is the tilt a tornado signature shows on.
                    var velocity = volume.Sweeps
                        .Where(s => s.Moment == Moment.Velocity)
                        .MinBy(s => s.ElevationIndex);
                    if (velocity is null) return null;

                    var computed = AzimuthalShear.Compute(VelocityDealiasing.Dealias(velocity));

                    // Without this the swath fills with speckle: a maximum taken over a
                    // dozen scans keeps every spurious gate any of them produced, and 89 %
                    // of strong-shear gates sit where there is no echo to have reflected.
                    if (GateQuality.ReflectivityFor(volume.Sweeps, velocity) is not { } reflectivity)
                        return computed;
                    // Clutter passes the echo test -- it returns strongly. On a quiet
                    // coastal night at KBOX the swath's peak was 0.0995 1/s of pure sea
                    // clutter, which reads as rotation; the CC test drops it to 0.0226.
                    return GateQuality.Mask(
                        computed, reflectivity,
                        GateQuality.CorrelationFor(volume.Sweeps, velocity));
                }, cts.Token);

                if (sweep is not null) shear.Add(sweep);
            }

            if (shear.Count == 0)
            {
                StatusChanged?.Invoke("No velocity data in those volumes — nothing to track.");
                return;
            }

            StatusChanged?.Invoke($"Rotation tracks: accumulating {shear.Count} scans…");
            var result = await Task.Run(() => RotationTracks.Accumulate(shear), cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            _result = result;
            Show(result);
            StatusChanged?.Invoke($"Rotation tracks: {Summary}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke($"Could not build rotation tracks: {ex.Message}");
        }
        finally
        {
            ProgressChanged?.Invoke(-1);
        }
    }

    public void Clear()
    {
        _cts?.Cancel();
        _result = null;
        mapView.SetImageOverlay(MapView.OverlaySlot.Analysis, null);
    }

    /// <summary>
    /// Colour the swath with the same scale the live product uses, so a track and a scan
    /// read the same way. Anything below the threshold is fully transparent rather than
    /// dark, so the radar underneath stays visible.
    /// </summary>
    private void Show(RotationTrackResult result)
    {
        var table = BuiltinTables.AzimuthalShear;
        var palette = table.BuildRgba256();
        float min = table.MinValue, range = table.Range;

        var bgra = new byte[result.Width * result.Height * 4];
        Parallel.For(0, result.Height, row =>
        {
            for (int column = 0; column < result.Width; column++)
            {
                int i = row * result.Width + column;
                float value = result.Values[i];
                if (float.IsNaN(value) || value < VisibleThreshold) continue;

                int level = (int)Math.Clamp((value - min) / range * 255f, 0, 255);
                int source = level * 4;
                int target = i * 4;
                bgra[target + 0] = palette[source + 2];
                bgra[target + 1] = palette[source + 1];
                bgra[target + 2] = palette[source + 0];
                bgra[target + 3] = palette[source + 3];
            }
        });

        mapView.SetImageOverlay(MapView.OverlaySlot.Analysis, new MapView.ImageOverlay(
            bgra, result.Width, result.Height,
            result.MinX, result.MinY, result.MaxX, result.MaxY, _opacity));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _archive.Dispose();
    }
}
