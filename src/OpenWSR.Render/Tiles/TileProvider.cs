using OpenWSR.Geo;

namespace OpenWSR.Render.Tiles;

/// <summary>An XYZ raster tile source. Provider and key are user-configurable from day one.</summary>
public sealed record TileProvider(string Name, string UrlTemplate, string UserAgent)
{
    public string UrlFor(TileKey key) => UrlTemplate
        .Replace("{z}", key.Z.ToString())
        .Replace("{x}", key.X.ToString())
        .Replace("{y}", key.Y.ToString());

    public static TileProvider Osm(string userAgent) => new(
        "osm", "https://tile.openstreetmap.org/{z}/{x}/{y}.png", userAgent);

    public static TileProvider MapTiler(string apiKey, string userAgent) => new(
        "maptiler",
        $"https://api.maptiler.com/maps/streets-v2/256/{{z}}/{{x}}/{{y}}.png?key={apiKey}",
        userAgent);

    /// <summary>
    /// Iowa State's pre-rendered NEXRAD base-reflectivity mosaic (N0Q). A seamless
    /// national picture without decoding GRIB2 ourselves; refreshed about every 5 minutes.
    /// </summary>
    public static TileProvider NexradMosaic(string userAgent) => new(
        "nexrad-mosaic",
        "https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/nexrad-n0q-900913/{z}/{x}/{y}.png",
        userAgent);
}
