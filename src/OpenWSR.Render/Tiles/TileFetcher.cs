using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenWSR.Geo;

namespace OpenWSR.Render.Tiles;

/// <summary>
/// Async tile loader: two-tier cache (disk under %LOCALAPPDATA%\OpenWSR\tiles, then network),
/// decoded to 256x256 BGRA. Completed tiles land in a queue the render thread drains.
/// </summary>
public sealed class TileFetcher : IDisposable
{
    private readonly TileProvider _provider;
    private readonly string _cacheRoot;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<TileKey, byte> _inFlight = new();
    private readonly ConcurrentQueue<(TileKey Key, byte[] Bgra)> _completed = new();
    private readonly ConcurrentDictionary<TileKey, DateTime> _failed = new();
    private readonly SemaphoreSlim _concurrency = new(4);
    private readonly CancellationTokenSource _cts = new();

    public TileFetcher(TileProvider provider, string? cacheRoot = null)
    {
        _provider = provider;
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenWSR", "tiles", provider.Name);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(provider.UserAgent);
    }

    /// <summary>Request a tile if it is not already loading; results appear in the completed queue.</summary>
    public void Request(TileKey key)
    {
        if (!key.IsValid) return;
        if (_failed.TryGetValue(key, out var when) && DateTime.UtcNow - when < TimeSpan.FromSeconds(30))
            return;
        if (!_inFlight.TryAdd(key, 0)) return;
        _ = LoadAsync(key);
    }

    public bool TryDequeueCompleted(out (TileKey Key, byte[] Bgra) item) => _completed.TryDequeue(out item);

    private int _failureCount;

    /// <summary>How many tile requests have failed outright.</summary>
    public int FailureCount => _failureCount;

    /// <summary>The first failure seen, provider and reason, or null while all is well.</summary>
    public string? FirstFailure { get; private set; }

    private async Task LoadAsync(TileKey key)
    {
        try
        {
            var diskPath = Path.Combine(_cacheRoot, key.Z.ToString(), key.X.ToString(), $"{key.Y}.png");
            byte[]? encoded = null;
            if (File.Exists(diskPath))
            {
                encoded = await File.ReadAllBytesAsync(diskPath, _cts.Token).ConfigureAwait(false);
            }
            else
            {
                await _concurrency.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    encoded = await _http.GetByteArrayAsync(_provider.UrlFor(key), _cts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _concurrency.Release();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
                await File.WriteAllBytesAsync(diskPath, encoded, _cts.Token).ConfigureAwait(false);
            }

            _completed.Enqueue((key, DecodeToBgra(encoded)));
            _failed.TryRemove(key, out _);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A tile source that fails every request looks exactly like one that is merely
            // slow, because the map just stays empty. Keeping the first failure lets the
            // caller say which layer is broken and why instead of guessing.
            _failed[key] = DateTime.UtcNow; // retried after a cool-down when still visible
            FirstFailure ??= $"{_provider.Name} {key}: {ex.GetType().Name}: {ex.Message}";
            Interlocked.Increment(ref _failureCount);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private static byte[] DecodeToBgra(byte[] encoded)
    {
        using var ms = new MemoryStream(encoded);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        if (frame.PixelWidth != TileMath.TilePixels || frame.PixelHeight != TileMath.TilePixels)
            frame = new TransformedBitmap(frame, new ScaleTransform(
                TileMath.TilePixels / (double)frame.PixelWidth,
                TileMath.TilePixels / (double)frame.PixelHeight));
        var pixels = new byte[TileMath.TilePixels * TileMath.TilePixels * 4];
        frame.CopyPixels(pixels, TileMath.TilePixels * 4, 0);
        return pixels;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}
