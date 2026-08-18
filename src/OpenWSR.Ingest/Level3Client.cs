using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OpenWSR.Nexrad.Level3;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>
/// Level III product access against unidata-nexrad-level3. Keys are
/// SITE_PROD_YYYY_MM_DD_HH_MM_SS with a 3-letter site (ICAO minus the leading K/P/T),
/// so lexicographic order is chronological — the newest product is the last key
/// under a date-bounded prefix.
/// </summary>
public sealed class Level3Client : IDisposable
{
    public const string Bucket = "unidata-nexrad-level3";
    private static readonly ILogger Log = Serilog.Log.ForContext<Level3Client>();

    private readonly AmazonS3Client _s3 = new(new AnonymousAWSCredentials(), new AmazonS3Config
    {
        RegionEndpoint = RegionEndpoint.USEast1,
        Timeout = TimeSpan.FromSeconds(20),
        MaxErrorRetry = 2,
    });

    public static string SitePrefix(string icao) =>
        icao.Length == 4 ? icao[1..].ToUpperInvariant() : icao.ToUpperInvariant();

    /// <summary>Fetch and decode the newest instance of a product (e.g. "NST") for a site, or null.</summary>
    public async Task<Level3Product?> GetLatestAsync(
        string icao, string product, CancellationToken ct = default)
    {
        var bytes = await GetLatestBytesAsync(icao, product, ct);
        return bytes is null ? null : Level3File.Decode(bytes);
    }

    /// <summary>Fetch and decode the newest digital radial-image product (e.g. "DVL"), or null.</summary>
    public async Task<RadialImageProduct?> GetLatestRadialImageAsync(
        string icao, string product, CancellationToken ct = default)
    {
        var bytes = await GetLatestBytesAsync(icao, product, ct);
        return bytes is null ? null : RadialImage.Decode(bytes);
    }

    private async Task<byte[]?> GetLatestBytesAsync(string icao, string product, CancellationToken ct)
    {
        var site = SitePrefix(icao);
        // Products only exist when the algorithm has output; probe today then yesterday.
        for (int daysBack = 0; daysBack <= 1; daysBack++)
        {
            var day = DateTime.UtcNow.AddDays(-daysBack);
            var prefix = $"{site}_{product}_{day:yyyy_MM_dd}_";
            string? newestKey = null;
            var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix };
            do
            {
                var response = await _s3.ListObjectsV2Async(request, ct);
                var objects = response.S3Objects;
                if (objects is { Count: > 0 })
                    newestKey = objects[^1].Key;
                request.ContinuationToken = response.NextContinuationToken;
            } while (request.ContinuationToken is not null);

            if (newestKey is null) continue;

            using var data = await _s3.GetObjectAsync(Bucket, newestKey, ct);
            using var buffer = new MemoryStream((int)data.ContentLength);
            await data.ResponseStream.CopyToAsync(buffer, ct);
            Log.Debug("Level3 {Key}: {Bytes} bytes", newestKey, buffer.Length);
            return buffer.ToArray();
        }
        return null;
    }

    public void Dispose() => _s3.Dispose();
}
