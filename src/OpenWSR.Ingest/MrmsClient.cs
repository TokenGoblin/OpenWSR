using System.Globalization;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>A fetched MRMS product: the raw GRIB2 bytes and the time they are valid for.</summary>
public sealed record MrmsProduct(byte[] Grib2, DateTime TimeUtc, string Key);

/// <summary>
/// Multi-Radar Multi-Sensor composites from the NOAA open-data bucket.
///
/// This is the same field the tile mosaic shows, but as numbers rather than someone
/// else's pictures — which matters because the pre-rendered tiles stop at zoom 12 and
/// are stretched above it, while the grid itself is 0.01° and stays sharp all the way in.
/// </summary>
public sealed class MrmsClient : IDisposable
{
    public const string Bucket = "noaa-mrms-pds";

    /// <summary>Merged composite reflectivity — the national mosaic everyone means.</summary>
    public const string CompositeReflectivity = "CONUS/MergedReflectivityQCComposite_00.50";

    private static readonly ILogger Log = Serilog.Log.ForContext<MrmsClient>();

    private readonly AmazonS3Client _s3 = new(new AnonymousAWSCredentials(), new AmazonS3Config
    {
        RegionEndpoint = RegionEndpoint.USEast1,
        Timeout = TimeSpan.FromSeconds(30),
        MaxErrorRetry = 2,
    });

    /// <summary>
    /// The newest composite available, or null when the bucket has nothing recent.
    ///
    /// Keys sort lexicographically by timestamp within a day, so the last key of the
    /// newest populated day wins. Just after midnight UTC today's folder can be empty or
    /// a few minutes behind, hence the walk back to yesterday.
    /// </summary>
    public async Task<MrmsProduct?> GetLatestAsync(
        string product = CompositeReflectivity, CancellationToken ct = default)
    {
        for (int daysBack = 0; daysBack <= 1; daysBack++)
        {
            var day = DateTime.UtcNow.AddDays(-daysBack);
            string prefix = $"{product}/{day:yyyyMMdd}/";

            string? newest = null;
            var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix };
            do
            {
                var response = await _s3.ListObjectsV2Async(request, ct);
                if (response.S3Objects is { Count: > 0 } objects)
                    newest = objects[^1].Key;
                request.ContinuationToken = response.NextContinuationToken;
            } while (request.ContinuationToken is not null);

            if (newest is null) continue;

            using var data = await _s3.GetObjectAsync(Bucket, newest, ct);
            using var buffer = new MemoryStream();
            await data.ResponseStream.CopyToAsync(buffer, ct);

            var time = TimeOf(newest) ?? DateTime.UtcNow;
            Log.Debug("MRMS {Key}: {Bytes} bytes, valid {Time:HH:mm:ss}Z",
                newest, buffer.Length, time);
            return new MrmsProduct(buffer.ToArray(), time, newest);
        }
        return null;
    }

    /// <summary>
    /// Valid time from a key ending <c>_YYYYMMDD-HHMMSS.grib2.gz</c>. Null when the name
    /// does not match, so a bucket layout change degrades to "unknown time" rather than
    /// a wrong one.
    /// </summary>
    public static DateTime? TimeOf(string key)
    {
        string name = key[(key.LastIndexOf('/') + 1)..];
        int dash = name.LastIndexOf('-');
        if (dash < 9 || dash + 7 > name.Length) return null;

        var date = name.AsSpan(dash - 8, 8);
        var time = name.AsSpan(dash + 1, 6);
        if (!DateTime.TryParseExact(
                string.Concat(date, time), "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
            return null;

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    public void Dispose() => _s3.Dispose();
}
