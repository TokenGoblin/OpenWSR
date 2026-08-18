using System.Globalization;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OpenWSR.Nexrad;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>One object in the real-time chunks bucket.</summary>
public sealed record ChunkRef(
    string Key, string SiteId, int VolumeNumber, DateTime VolumeStartUtc,
    int Sequence, ChunkType Type, DateTime LastModifiedUtc)
{
    /// <summary>Chunks of one volume share the volume-start timestamp in their key.</summary>
    public (int, DateTime) VolumeIdentity => (VolumeNumber, VolumeStartUtc);
}

/// <summary>
/// Real-time Level II access against unidata-nexrad-level2-chunks. The 999 volume
/// directories rotate with no "latest" pointer: discovery probes directories for the
/// newest LastModified, coarse-to-fine, and the result is cached (and persisted) so
/// steady-state polling is one LIST per poll.
/// </summary>
public sealed class ChunksClient : IDisposable
{
    public const string Bucket = "unidata-nexrad-level2-chunks";

    private readonly AmazonS3Client _s3;
    private static readonly ILogger Log = Serilog.Log.ForContext<ChunksClient>();

    public ChunksClient()
    {
        _s3 = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.USEast1,
            Timeout = TimeSpan.FromSeconds(20),
            MaxErrorRetry = 2,
        });
    }

    public static bool TryParseKey(string key, DateTime lastModifiedUtc, out ChunkRef chunk)
    {
        // <SITE>/<volume 1-999>/<YYYYMMDD-HHMMSS>-<seq>-<S|I|E>
        chunk = null!;
        var parts = key.Split('/');
        if (parts.Length != 3) return false;
        var name = parts[2].Split('-');
        if (name.Length != 4) return false;
        if (!int.TryParse(parts[1], out int volume)) return false;
        if (!DateTime.TryParseExact($"{name[0]}-{name[1]}", "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
            return false;
        if (!int.TryParse(name[2], out int seq)) return false;
        var type = name[3] switch
        {
            "S" => ChunkType.Start,
            "I" => ChunkType.Intermediate,
            "E" => ChunkType.End,
            _ => (ChunkType)(-1),
        };
        if ((int)type == -1) return false;
        chunk = new ChunkRef(key, parts[0], volume, start, seq, type, lastModifiedUtc);
        return true;
    }

    /// <summary>All chunks currently in one volume directory, ascending by sequence.</summary>
    public async Task<IReadOnlyList<ChunkRef>> ListVolumeDirAsync(
        string siteId, int volumeNumber, CancellationToken ct = default)
    {
        var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket,
            Prefix = $"{siteId.ToUpperInvariant()}/{volumeNumber}/",
        }, ct);

        var chunks = new List<ChunkRef>();
        foreach (var s3Object in response.S3Objects ?? [])
            if (TryParseKey(s3Object.Key, s3Object.LastModified?.ToUniversalTime() ?? default, out var chunk))
                chunks.Add(chunk);
        chunks.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        return chunks;
    }

    public async Task<byte[]> FetchChunkAsync(ChunkRef chunk, CancellationToken ct = default)
    {
        using var response = await _s3.GetObjectAsync(Bucket, chunk.Key, ct);
        using var buffer = new MemoryStream((int)response.ContentLength);
        await response.ResponseStream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>
    /// Locate the volume directory with the newest data. Starts from a hint when given
    /// (steady state: 1–3 LISTs); otherwise probes every 50th directory, then refines
    /// (~30 LISTs cold).
    /// </summary>
    public async Task<int> FindActiveVolumeAsync(string siteId, int? hint, CancellationToken ct = default)
    {
        siteId = siteId.ToUpperInvariant();

        if (hint is { } start)
        {
            // Walk forward from the hint while newer directories have content.
            int best = start;
            var bestTime = await NewestInDirAsync(siteId, best, ct) ?? DateTime.MinValue;
            for (int step = 1; step <= 3; step++)
            {
                int candidate = Wrap(start + step);
                var time = await NewestInDirAsync(siteId, candidate, ct);
                if (time > bestTime)
                {
                    best = candidate;
                    bestTime = time.Value;
                }
            }
            if (bestTime > DateTime.MinValue && DateTime.UtcNow - bestTime < TimeSpan.FromMinutes(30))
                return best;
            // Hint stale — fall through to the full probe.
        }

        var (coarseBest, coarseTime) = await ProbeAsync(siteId, Enumerable.Range(0, 20).Select(i => 1 + i * 50), ct);
        var refineCandidates = Enumerable.Range(-49, 99).Select(o => Wrap(coarseBest + o));
        var (fineBest, fineTime) = await ProbeAsync(siteId, refineCandidates.Where((_, i) => i % 5 == 0), ct);
        var finalCandidates = Enumerable.Range(-5, 11).Select(o => Wrap(fineBest + o));
        var (best2, bestTime2) = await ProbeAsync(siteId, finalCandidates, ct);

        Log.Information("Active volume for {Site}: {Volume} (newest {Time:u}, coarse {Coarse:u})",
            siteId, best2, bestTime2, coarseTime);
        if (bestTime2 == DateTime.MinValue)
            throw new InvalidOperationException($"No chunk data found for site {siteId}.");
        return best2;
    }

    private async Task<(int Volume, DateTime Newest)> ProbeAsync(
        string siteId, IEnumerable<int> volumes, CancellationToken ct)
    {
        int best = 1;
        var bestTime = DateTime.MinValue;
        foreach (var volume in volumes.Distinct())
        {
            var time = await NewestInDirAsync(siteId, volume, ct);
            if (time > bestTime)
            {
                best = volume;
                bestTime = time.Value;
            }
        }
        return (best, bestTime);
    }

    private async Task<DateTime?> NewestInDirAsync(string siteId, int volume, CancellationToken ct)
    {
        var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket,
            Prefix = $"{siteId}/{volume}/",
            MaxKeys = 100,
        }, ct);
        var objects = response.S3Objects;
        if (objects is not { Count: > 0 }) return null;
        return objects.Max(o => o.LastModified?.ToUniversalTime() ?? DateTime.MinValue);
    }

    private static int Wrap(int volume) => ((volume - 1) % 999 + 999) % 999 + 1;

    public void Dispose() => _s3.Dispose();
}
