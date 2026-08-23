using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>A fetched ABI product: the raw NetCDF-4 bytes and the key they came from.</summary>
public sealed record AbiProduct(byte[] NetCdf, DateTime StartUtc, string Key);

/// <summary>
/// GOES ABI Cloud and Moisture Imagery from the NOAA open-data buckets.
///
/// This is the same picture the pre-rendered satellite tiles show, but as measurements
/// rather than someone else's rendering of them — which buys the choice of band, the choice
/// of enhancement, the real five-minute cadence, and independence from a tile server whose
/// paths have already moved once.
///
/// CONUS is the sector worth asking for: five minutes, 2 km, and about four megabytes for a
/// single band. Full disk is ten minutes and far larger for ground this app never shows.
/// </summary>
public sealed class AbiClient : IDisposable
{
    /// <summary>GOES-19 has been GOES-East since 2025; GOES-18 sits west.</summary>
    public const string EastBucket = "noaa-goes19";

    public const string WestBucket = "noaa-goes18";

    /// <summary>Clean longwave infrared, 10.3 µm — cloud tops day and night.</summary>
    public const int CleanInfrared = 13;

    /// <summary>Red visible, 0.64 µm and 0.5 km. Four times the detail, daylight only.</summary>
    public const int RedVisible = 2;

    private static readonly ILogger Log = Serilog.Log.ForContext<AbiClient>();

    private readonly AmazonS3Client _s3 = new(new AnonymousAWSCredentials(), new AmazonS3Config
    {
        RegionEndpoint = RegionEndpoint.USEast1,
        Timeout = TimeSpan.FromSeconds(60),
        MaxErrorRetry = 2,
    });

    /// <summary>
    /// The newest CONUS image of one band, or null when the bucket has nothing recent.
    ///
    /// Keys are prefixed by hour and sort by start time within it, so the last key of the
    /// newest populated hour wins. Products land two to four minutes behind the scan, so the
    /// current hour is routinely empty for its first minutes and the previous one is walked
    /// back to — the same shape as the MRMS and lightning readers.
    /// </summary>
    public async Task<AbiProduct?> GetLatestAsync(
        int band = CleanInfrared, string bucket = EastBucket, CancellationToken ct = default)
    {
        for (int hoursBack = 0; hoursBack <= 2; hoursBack++)
        {
            var hour = DateTime.UtcNow.AddHours(-hoursBack);
            // OR_ABI-L2-CMIPC-M6C13_G19_sYYYYDDDHHMMSSS_e..._c....nc — the mode digit after
            // M varies with the scan schedule, so the prefix stops before it.
            string prefix = $"ABI-L2-CMIPC/{hour:yyyy}/{hour.DayOfYear:D3}/{hour:HH}/";

            string? newest = null;
            var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
            do
            {
                var response = await _s3.ListObjectsV2Async(request, ct);
                foreach (var o in response.S3Objects ?? [])
                    if (BandOf(o.Key) == band && (newest is null || StringComparer.Ordinal.Compare(o.Key, newest) > 0))
                        newest = o.Key;
                request.ContinuationToken = response.NextContinuationToken;
            } while (request.ContinuationToken is not null);

            if (newest is null) continue;

            using var data = await _s3.GetObjectAsync(bucket, newest, ct);
            using var buffer = new MemoryStream();
            await data.ResponseStream.CopyToAsync(buffer, ct);

            var start = StartTimeOf(newest) ?? DateTime.UtcNow;
            Log.Debug("ABI C{Band:D2} {Key}: {Bytes} bytes, scan start {Time:HH:mm:ss}Z",
                band, newest, buffer.Length, start);
            return new AbiProduct(buffer.ToArray(), start, newest);
        }

        Log.Warning("ABI: no C{Band:D2} imagery in the last three hours of {Bucket}", band, bucket);
        return null;
    }

    /// <summary>The band number out of the <c>-M6C13_</c> token, or -1 if the key is not one.</summary>
    internal static int BandOf(string key)
    {
        int marker = key.IndexOf("-M", StringComparison.Ordinal);
        // "-M", a mode digit, "C", then two band digits.
        if (marker < 0 || marker + 5 >= key.Length) return -1;
        if (key[marker + 3] != 'C') return -1;
        return int.TryParse(key.AsSpan(marker + 4, 2), out int band) ? band : -1;
    }

    /// <summary>
    /// Scan start from the <c>_sYYYYDDDHHMMSSS_</c> token. The last digit is tenths of a
    /// second, which nothing here needs but which makes the field fourteen long rather than
    /// the thirteen a reader expecting whole seconds would take.
    /// </summary>
    internal static DateTime? StartTimeOf(string key)
    {
        int marker = key.IndexOf("_s", StringComparison.Ordinal);
        if (marker < 0 || marker + 16 > key.Length) return null;
        var stamp = key.AsSpan(marker + 2, 14);

        if (!int.TryParse(stamp[..4], out int year) ||
            !int.TryParse(stamp.Slice(4, 3), out int dayOfYear) ||
            !int.TryParse(stamp.Slice(7, 2), out int hour) ||
            !int.TryParse(stamp.Slice(9, 2), out int minute) ||
            !int.TryParse(stamp.Slice(11, 2), out int second))
            return null;
        if (dayOfYear is < 1 or > 366 || hour > 23 || minute > 59 || second > 59) return null;

        return new DateTime(year, 1, 1, hour, minute, second, DateTimeKind.Utc)
            .AddDays(dayOfYear - 1);
    }

    public void Dispose() => _s3.Dispose();
}
