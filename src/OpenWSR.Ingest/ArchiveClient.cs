using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Serilog;

namespace OpenWSR.Ingest;

public sealed record ArchiveVolumeRef(string Key, string SiteId, DateTime TimeUtc, long SizeBytes);

/// <summary>
/// Level II archive access: anonymous S3 against unidata-nexrad-level2 (us-east-1),
/// with retry/backoff, request logging, and the disk cache in front of every download.
/// </summary>
public sealed class ArchiveClient : IDisposable
{
    public const string Bucket = "unidata-nexrad-level2";

    private readonly AmazonS3Client _s3;
    private readonly VolumeCache _cache;
    private static readonly ILogger Log = Serilog.Log.ForContext<ArchiveClient>();

    public ArchiveClient(VolumeCache? cache = null)
    {
        _cache = cache ?? new VolumeCache();
        _s3 = new AmazonS3Client(new AnonymousAWSCredentials(), new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.USEast1,
            Timeout = TimeSpan.FromSeconds(30),
            MaxErrorRetry = 0, // retries are ours, with backoff and logging
        });
    }

    /// <summary>All volumes for a site on a UTC date, ascending by time. Skips *_MDM objects.</summary>
    public async Task<IReadOnlyList<ArchiveVolumeRef>> ListVolumesAsync(
        string siteId, DateOnly dateUtc, CancellationToken ct = default)
    {
        var prefix = $"{dateUtc:yyyy/MM/dd}/{siteId.ToUpperInvariant()}/";
        var results = new List<ArchiveVolumeRef>();

        var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix };
        do
        {
            var response = await WithRetry(
                () => _s3.ListObjectsV2Async(request, ct), $"LIST {prefix}", ct);
            foreach (var s3Object in response.S3Objects ?? [])
            {
                if (s3Object.Key.EndsWith("_MDM", StringComparison.Ordinal))
                    continue; // metadata-only objects, not decodable volumes
                if (TryParseTime(s3Object.Key, out var time))
                    results.Add(new ArchiveVolumeRef(
                        s3Object.Key, siteId.ToUpperInvariant(), time, s3Object.Size ?? 0));
            }
            request.ContinuationToken = response.NextContinuationToken;
        } while (request.ContinuationToken is not null);

        results.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
        return results;
    }

    /// <summary>Fetch a volume to the local cache (no network when already cached) and return its path.</summary>
    public async Task<string> FetchVolumeAsync(ArchiveVolumeRef volume, CancellationToken ct = default)
    {
        if (_cache.TryGet(volume.Key) is { } cached)
            return cached;

        var temp = Path.Combine(Path.GetTempPath(), $"openwsr-{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await WithRetry(
                () => _s3.GetObjectAsync(Bucket, volume.Key, ct), $"GET {volume.Key}", ct);
            await using (var file = File.Create(temp))
                await response.ResponseStream.CopyToAsync(file, ct);
            return _cache.Store(volume.Key, temp);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static async Task<T> WithRetry<T>(
        Func<Task<T>> operation, string description, CancellationToken ct, int attempts = 4)
    {
        for (int attempt = 1; ; attempt++)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await operation();
                Log.Debug("S3 {Operation} ok in {Ms} ms", description, started.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                Log.Warning(ex, "S3 {Operation} failed (attempt {Attempt}/{Max}), retrying in {Delay}",
                    description, attempt, attempts, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    internal static bool TryParseTime(string key, out DateTime timeUtc)
    {
        // .../KTLX20130520_201643_V06[.gz]
        timeUtc = default;
        var name = key[(key.LastIndexOf('/') + 1)..];
        if (name.Length < 19) return false;
        return DateTime.TryParseExact(
            name.Substring(4, 15), "yyyyMMdd_HHmmss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out timeUtc);
    }

    public void Dispose() => _s3.Dispose();
}
