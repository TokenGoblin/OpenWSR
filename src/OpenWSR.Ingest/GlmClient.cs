using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OpenWSR.NetCdf;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>
/// GOES Lightning Mapper flashes from the NOAA open-data buckets.
///
/// GOES-19 is the eastern satellite and covers the Americas; GOES-18 sits west and is
/// only worth asking for the Pacific. Files arrive every twenty seconds, so a few minutes
/// of lightning is a handful of small reads rather than one big one.
/// </summary>
public sealed class GlmClient : IDisposable
{
    /// <summary>GOES-East since 2025. GOES-16 still exists in S3 but stopped being filled.</summary>
    public const string EastBucket = "noaa-goes19";

    public const string WestBucket = "noaa-goes18";

    private static readonly ILogger Log = Serilog.Log.ForContext<GlmClient>();

    private readonly AmazonS3Client _s3 = new(new AnonymousAWSCredentials(), new AmazonS3Config
    {
        RegionEndpoint = RegionEndpoint.USEast1,
        Timeout = TimeSpan.FromSeconds(20),
        MaxErrorRetry = 2,
    });

    /// <summary>
    /// Every flash in roughly the last <paramref name="minutes"/> minutes.
    ///
    /// Products publish a couple of minutes behind real time, so the window is walked back
    /// from a little before now; asking for the current minute reliably returns nothing.
    /// </summary>
    public async Task<IReadOnlyList<LightningFlash>> GetRecentAsync(
        int minutes = 10, string bucket = EastBucket, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.AddMinutes(-2);
        var keys = new List<string>();

        // Keys are GLM-L2-LCFA/yyyy/ddd/HH/..., so an hour is one prefix. A window
        // spanning the top of an hour needs both.
        foreach (var hour in HoursCovering(now, minutes))
        {
            string prefix = $"GLM-L2-LCFA/{hour:yyyy}/{hour.DayOfYear:D3}/{hour:HH}/";
            var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
            do
            {
                var response = await _s3.ListObjectsV2Async(request, ct);
                foreach (var o in response.S3Objects ?? [])
                {
                    if (StartTimeOf(o.Key) is { } start && start >= now.AddMinutes(-minutes))
                        keys.Add(o.Key);
                }
                request.ContinuationToken = response.NextContinuationToken;
            } while (request.ContinuationToken is not null);
        }

        Log.Debug("GLM: {Count} files in the last {Minutes} min from {Bucket}",
            keys.Count, minutes, bucket);

        // Bounded parallelism: a ten-minute window is thirty files of a quarter megabyte.
        var flashes = new List<LightningFlash>();
        var gate = new SemaphoreSlim(6);
        var tasks = keys.Select(async key =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var data = await _s3.GetObjectAsync(bucket, key, ct);
                using var buffer = new MemoryStream();
                await data.ResponseStream.CopyToAsync(buffer, ct);
                return GlmFile.DecodeFlashes(buffer.ToArray());
            }
            catch (Hdf5FormatException ex)
            {
                // One malformed file should not lose the other twenty-nine.
                Log.Warning(ex, "GLM decode failed: {Key}", key);
                return [];
            }
            finally { gate.Release(); }
        });

        foreach (var batch in await Task.WhenAll(tasks))
            flashes.AddRange(batch);
        return flashes;
    }

    /// <summary>The UTC hours a window touches, oldest first.</summary>
    private static IEnumerable<DateTime> HoursCovering(DateTime end, int minutes)
    {
        var start = end.AddMinutes(-minutes);
        var hour = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        while (hour <= end)
        {
            yield return hour;
            hour = hour.AddHours(1);
        }
    }

    /// <summary>
    /// Pull the scan start out of a key: ..._sYYYYDDDHHMMSSt.nc, where the trailing digit
    /// is tenths of a second.
    /// </summary>
    public static DateTime? StartTimeOf(string key)
    {
        int i = key.IndexOf("_s", StringComparison.Ordinal);
        if (i < 0 || i + 15 > key.Length) return null;
        var span = key.AsSpan(i + 2, 13);

        if (!int.TryParse(span[..4], out int year) ||
            !int.TryParse(span.Slice(4, 3), out int dayOfYear) ||
            !int.TryParse(span.Slice(7, 2), out int hour) ||
            !int.TryParse(span.Slice(9, 2), out int minute) ||
            !int.TryParse(span.Slice(11, 2), out int second))
            return null;
        if (dayOfYear is < 1 or > 366) return null;

        return new DateTime(year, 1, 1, hour, minute, second, DateTimeKind.Utc)
            .AddDays(dayOfYear - 1);
    }

    public void Dispose() => _s3.Dispose();
}
