using System.Globalization;
using System.Net.Http;
using OpenWSR.Grib2;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>One forecast frame of simulated composite reflectivity.</summary>
public sealed record ForecastFrame(DateTime ValidTimeUtc, int ForecastHour, Grib2Field Field);

/// <summary>
/// HRRR simulated composite reflectivity from AWS Open Data — the basis for future
/// radar. Each cycle's GRIB2 file is many megabytes, but its .idx sidecar lets us
/// byte-range just the REFC record, which is about 140 kB per forecast hour.
/// </summary>
public sealed class HrrrClient : IDisposable
{
    private const string Bucket = "https://noaa-hrrr-bdp-pds.s3.amazonaws.com";
    private static readonly ILogger Log = Serilog.Log.ForContext<HrrrClient>();
    private readonly HttpClient _http;

    public HrrrClient(string userAgent)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>
    /// Fetch forecast hours 1..<paramref name="hours"/> from the newest cycle that has
    /// published them. HRRR runs hourly but takes roughly an hour to land, so the
    /// search walks back a few cycles before giving up.
    /// </summary>
    public async Task<IReadOnlyList<ForecastFrame>> GetForecastAsync(
        int hours = 6, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        for (int cycleBack = 1; cycleBack <= 5; cycleBack++)
        {
            var cycle = DateTime.UtcNow.AddHours(-cycleBack);
            cycle = new DateTime(cycle.Year, cycle.Month, cycle.Day, cycle.Hour, 0, 0, DateTimeKind.Utc);
            var frames = await TryCycleAsync(cycle, hours, progress, ct);
            if (frames.Count > 0)
            {
                Log.Information("HRRR cycle {Cycle:yyyy-MM-dd HH}z: {Count} frames", cycle, frames.Count);
                return frames;
            }
        }
        return [];
    }

    private async Task<IReadOnlyList<ForecastFrame>> TryCycleAsync(
        DateTime cycle, int hours, IProgress<(int, int)>? progress, CancellationToken ct)
    {
        var frames = new List<ForecastFrame>();
        for (int hour = 1; hour <= hours; hour++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{Bucket}/hrrr.{cycle:yyyyMMdd}/conus/hrrr.t{cycle:HH}z.wrfsfcf{hour:D2}.grib2";
            try
            {
                var range = await FindRefcRangeAsync(url + ".idx", ct);
                if (range is not { } r) return frames; // cycle incomplete; use what we have

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(r.Start, r.End);
                using var response = await _http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);

                var field = Grib2File.Decode(bytes);
                frames.Add(new ForecastFrame(cycle.AddHours(hour), hour, field));
                progress?.Report((hour, hours));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "HRRR f{Hour:D2} unavailable for {Cycle:HH}z", hour, cycle);
                return frames;
            }
        }
        return frames;
    }

    /// <summary>
    /// The .idx is one line per record: "n:byteOffset:d=...:REFC:entire atmosphere:...".
    /// The record ends where the next one begins.
    /// </summary>
    private async Task<(long Start, long End)?> FindRefcRangeAsync(string idxUrl, CancellationToken ct)
    {
        var text = await _http.GetStringAsync(idxUrl, ct);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(":REFC:", StringComparison.Ordinal)) continue;
            var fields = lines[i].Split(':');
            if (fields.Length < 2 ||
                !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long start))
                return null;
            long end = start + 4_000_000; // generous fallback for the final record
            if (i + 1 < lines.Length)
            {
                var next = lines[i + 1].Split(':');
                if (next.Length >= 2 &&
                    long.TryParse(next[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long nextStart))
                    end = nextStart - 1;
            }
            return (start, end);
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
