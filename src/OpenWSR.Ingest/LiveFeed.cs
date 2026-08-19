using System.Text.Json;
using System.Threading.Channels;
using OpenWSR.Nexrad;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>
/// Live Level II pipeline: poller → decoder over a bounded channel, per the build plan's
/// threading rules (nothing decodes on the UI or render thread). Emits renderable
/// partial volumes as cuts complete — the lowest tilt shows as soon as it exists.
/// </summary>
public sealed class LiveFeed : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LiveFeed>();

    private readonly ChunksClient _client = new();
    private CancellationTokenSource? _cts;
    private Task? _poller;
    private Task? _decoder;

    /// <summary>Raised on a thread-pool thread with a renderable snapshot.</summary>
    public event Action<RadarVolume, bool>? VolumeUpdated;
    public event Action<string>? StatusChanged;

    public DateTime? LastDataUtc { get; private set; }
    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>Point the feed at a site, draining any feed already running.</summary>
    public async Task StartAsync(string siteId)
    {
        await StopAsync();
        var cts = _cts = new CancellationTokenSource();
        var channel = Channel.CreateBounded<(ChunkRef Ref, byte[] Bytes)>(
            new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
        _poller = Task.Run(() => PollLoopAsync(siteId.ToUpperInvariant(), channel.Writer, cts.Token));
        _decoder = Task.Run(() => DecodeLoopAsync(channel.Reader, cts.Token));
    }

    /// <summary>
    /// Cancel the pipeline and wait for it to drain. Awaitable rather than blocking: an
    /// in-flight S3 request can hold the poller for seconds, and every caller is on the
    /// UI thread.
    /// </summary>
    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;
        var pending = Task.WhenAll(_poller ?? Task.CompletedTask, _decoder ?? Task.CompletedTask);
        _cts = null;
        _poller = _decoder = null;

        await cts.CancelAsync();
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Cancellation and the timeout are both expected; the tasks are abandoned.
        }
        cts.Dispose();
    }

    private async Task PollLoopAsync(
        string siteId, ChannelWriter<(ChunkRef, byte[])> writer, CancellationToken ct)
    {
        var seenKeys = new HashSet<string>();
        var delay = TimeSpan.FromSeconds(2);
        int? active = null;
        int quietPolls = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (active is null)
                    {
                        StatusChanged?.Invoke($"Locating live feed for {siteId}…");
                        active = await _client.FindActiveVolumeAsync(siteId, LoadHint(siteId), ct);
                        SaveHint(siteId, active.Value);
                        StatusChanged?.Invoke($"Live: {siteId} volume dir {active}");
                    }

                    var chunks = await _client.ListVolumeDirAsync(siteId, active.Value, ct);
                    int fresh = 0;
                    foreach (var chunk in chunks.Where(c => seenKeys.Add(c.Key)))
                    {
                        var bytes = await _client.FetchChunkAsync(chunk, ct);
                        await writer.WriteAsync((chunk, bytes), ct);
                        fresh++;
                    }

                    bool volumeEnded = chunks.Any(c => c.Type == ChunkType.End);
                    if (volumeEnded || fresh == 0)
                    {
                        // A new volume may have started in the next directory.
                        int next = active.Value % 999 + 1;
                        var nextChunks = await _client.ListVolumeDirAsync(siteId, next, ct);
                        var newest = nextChunks.Where(c => c.LastModifiedUtc != default)
                            .Select(c => c.LastModifiedUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                        if (newest > DateTime.UtcNow - TimeSpan.FromMinutes(20))
                        {
                            active = next;
                            SaveHint(siteId, next);
                            foreach (var chunk in nextChunks.Where(c => seenKeys.Add(c.Key)))
                            {
                                var bytes = await _client.FetchChunkAsync(chunk, ct);
                                await writer.WriteAsync((chunk, bytes), ct);
                                fresh++;
                            }
                        }
                    }

                    // Adaptive cadence: tight while chunks flow, backed off when idle.
                    if (fresh > 0)
                    {
                        quietPolls = 0;
                        delay = TimeSpan.FromSeconds(2);
                    }
                    else if (++quietPolls > 3)
                    {
                        delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 10));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Live poll failed for {Site}", siteId);
                    StatusChanged?.Invoke($"Live feed error ({ex.Message}) — retrying…");
                    delay = TimeSpan.FromSeconds(5);
                }
                await Task.Delay(delay, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task DecodeLoopAsync(
        ChannelReader<(ChunkRef Ref, byte[] Bytes)> reader, CancellationToken ct)
    {
        var assemblers = new Dictionary<(int, DateTime), LiveVolumeAssembler>();
        var emittedCuts = new Dictionary<(int, DateTime), int>();

        try
        {
            await foreach (var (chunk, bytes) in reader.ReadAllAsync(ct))
            {
                if (!assemblers.TryGetValue(chunk.VolumeIdentity, out var assembler))
                {
                    assemblers[chunk.VolumeIdentity] = assembler = new LiveVolumeAssembler();
                    emittedCuts[chunk.VolumeIdentity] = 0;

                    // A new volume can start before the old completes; keep only the two newest.
                    if (assemblers.Count > 2)
                    {
                        var oldest = assemblers.Keys.MinBy(k => k.Item2);
                        assemblers.Remove(oldest);
                        emittedCuts.Remove(oldest);
                    }
                }

                try
                {
                    if (!assembler.AddChunk(chunk.Sequence, chunk.Type, bytes))
                        continue; // duplicate
                }
                catch (NexradFormatException ex)
                {
                    Log.Warning(ex, "Chunk decode failed: {Key}", chunk.Key);
                    continue;
                }

                LastDataUtc = DateTime.UtcNow;

                bool complete = assembler.SawEnd;
                int cutsDone = assembler.CompletedCuts.Count;
                if (!assembler.HasData || (!complete && cutsDone <= emittedCuts[chunk.VolumeIdentity]))
                    continue;
                emittedCuts[chunk.VolumeIdentity] = cutsDone;

                try
                {
                    var snapshot = assembler.BuildSnapshot();
                    VolumeUpdated?.Invoke(snapshot, complete);
                    StatusChanged?.Invoke(
                        $"Live {snapshot.SiteId}: volume {chunk.VolumeStartUtc:HH:mm:ss}Z, " +
                        $"{cutsDone} cut(s) complete{(complete ? " — volume done" : "")}");
                }
                catch (NexradFormatException)
                {
                    // No RVOL yet (joined mid-record) — wait for more chunks.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- last-known volume directory hints, persisted across runs ----

    private static string HintPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenWSR", "live-state.json");

    private static int? LoadHint(string siteId)
    {
        try
        {
            if (File.Exists(HintPath) &&
                JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(HintPath))
                    is { } hints && hints.TryGetValue(siteId, out int hint))
                return hint;
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static void SaveHint(string siteId, int volume)
    {
        try
        {
            Dictionary<string, int> hints = [];
            if (File.Exists(HintPath))
                hints = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(HintPath)) ?? [];
            hints[siteId] = volume;
            Directory.CreateDirectory(Path.GetDirectoryName(HintPath)!);
            File.WriteAllText(HintPath, JsonSerializer.Serialize(hints));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not persist live-feed hint");
        }
    }

    public void Dispose()
    {
        var cts = _cts;
        var pending = Task.WhenAll(_poller ?? Task.CompletedTask, _decoder ?? Task.CompletedTask);
        _cts = null;
        _poller = _decoder = null;
        cts?.Cancel();

        // Close the S3 client only once the poller has let go of it — disposing it out
        // from under an in-flight request throws inside the loop for no benefit.
        pending.ContinueWith(_ =>
        {
            _client.Dispose();
            cts?.Dispose();
        }, TaskScheduler.Default);
    }
}
