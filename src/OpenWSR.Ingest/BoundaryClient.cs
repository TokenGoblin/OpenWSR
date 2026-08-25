using System.IO.Compression;
using System.Net.Http;
using OpenWSR.Geo;
using Serilog;

namespace OpenWSR.Ingest;

/// <summary>Which set of political boundaries to draw.</summary>
public enum BoundarySet
{
    States,
    Counties,
}

/// <summary>
/// Fetches US Census cartographic boundary files and caches them on disk.
/// </summary>
/// <remarks>
/// Boundaries do not change, so this downloads once and then never touches the network
/// again — unlike every other client here, which polls. The cache is a plain directory
/// under %LOCALAPPDATA%\OpenWSR\boundaries rather than the LRU volume cache, because these
/// must not be evicted: a user who turned the layer on offline should still have it.
///
/// <para>The 1:500,000 series is used rather than the 1:20,000,000 one. A radar view is
/// usually 100-300 km across, and at that scale the coarse file's county lines are visibly
/// polygonal — it exists for national thumbnails. The cost is 1.03 million points across
/// 3,235 counties, which is why nothing draws them without simplifying first.</para>
///
/// <para>Census publishes these as zipped shapefiles, so the archive is unpacked and only
/// the .shp and .dbf kept; the rest is metadata XML that outweighs the data.</para>
/// </remarks>
public sealed class BoundaryClient : IDisposable
{
    private const string Base = "https://www2.census.gov/geo/tiger/GENZ2023/shp";
    private static readonly ILogger Log = Serilog.Log.ForContext<BoundaryClient>();

    private readonly HttpClient _http;
    private readonly string _root;

    public BoundaryClient(string userAgent, string? root = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenWSR", "boundaries");
    }

    private static string NameOf(BoundarySet set) => set switch
    {
        BoundarySet.States => "cb_2023_us_state_500k",
        BoundarySet.Counties => "cb_2023_us_county_500k",
        _ => throw new ArgumentOutOfRangeException(nameof(set)),
    };

    /// <summary>True when the set is already on disk and needs no download.</summary>
    public bool IsCached(BoundarySet set)
    {
        var name = NameOf(set);
        return File.Exists(Path.Combine(_root, name + ".shp"))
            && File.Exists(Path.Combine(_root, name + ".dbf"));
    }

    /// <summary>
    /// The boundaries, from disk when they are there and from Census when they are not.
    /// </summary>
    public async Task<IReadOnlyList<ShapeFeature>> GetAsync(
        BoundarySet set, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var name = NameOf(set);
        var shp = Path.Combine(_root, name + ".shp");
        var dbf = Path.Combine(_root, name + ".dbf");

        if (!IsCached(set))
        {
            progress?.Report($"Downloading {Label(set)} boundaries from the US Census…");
            await DownloadAsync(name, token).ConfigureAwait(false);
        }

        progress?.Report($"Reading {Label(set)} boundaries…");
        var features = await Task.Run(
            () => Shapefile.Read(File.ReadAllBytes(shp), File.ReadAllBytes(dbf)), token)
            .ConfigureAwait(false);

        Log.Information("Boundaries {Set}: {Count} shapes, {Points} points",
            set, features.Count, features.Sum(f => f.Parts.Sum(p => p.Count)));
        return features;
    }

    private static string Label(BoundarySet set) =>
        set == BoundarySet.States ? "state" : "county";

    private async Task DownloadAsync(string name, CancellationToken token)
    {
        Directory.CreateDirectory(_root);
        var url = $"{Base}/{name}.zip";

        using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        int written = 0;
        foreach (var entry in archive.Entries)
        {
            var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (extension is not (".shp" or ".dbf")) continue;

            // Write beside the target and move, so a download interrupted halfway cannot
            // leave a truncated file that looks cached.
            var target = Path.Combine(_root, entry.Name);
            var temp = target + ".part";
            await using (var source = entry.Open())
            await using (var destination = File.Create(temp))
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            written++;
        }

        if (written < 2)
            throw new InvalidDataException(
                $"{name}.zip did not contain both a .shp and a .dbf.");
    }

    public void Dispose() => _http.Dispose();
}
