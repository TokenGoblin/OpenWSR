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

    /// <summary>
    /// CARTO's "Dark Matter" — OpenStreetMap data rendered near-black.
    ///
    /// The point is contrast. Reflectivity is a bright, saturated palette, and on the standard
    /// OSM style it competes with green landcover, blue water and orange roads for the same
    /// part of the eye. Against a near-black ground the weather is the only bright thing on
    /// screen, which is why every broadcast and consumer radar app looks like this.
    ///
    /// Free and keyless, unlike the other dark styles worth having — Stadia and MapTiler both
    /// want an API key, and Esri's dark canvas is a mid-grey that gives up most of the
    /// contrast. Attribution is required and names CARTO as well as OpenStreetMap.
    /// </summary>
    public static TileProvider CartoDark(string userAgent) => new(
        "carto-dark", "https://basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png", userAgent);

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

    /// <summary>
    /// GOES-East infrared from Iowa State — cloud tops day and night, unlike visible.
    /// Pre-rendered XYZ, so it drops straight into the tile pipeline.
    /// </summary>
    /// <remarks>
    /// The layer is <c>goes_east_conus_ch13</c>, not <c>goes_east_ch13</c>. This is the third
    /// IEM path to drift under this project, and the one that hid best: an unknown layer name
    /// is answered with <b>HTTP 200 and a valid PNG</b> reading "Invalid TMS Request", so
    /// nothing in the fetch path could tell it from imagery — the map simply went solid red,
    /// and 5 MB of the error tile was cached to disk as though it were data.
    ///
    /// Two things follow from that, and both matter more than the URL itself. Check a tile
    /// source by fetching <em>two different tiles</em> and confirming the bytes differ; a
    /// status code proves nothing here. And the cache directory is named for the layer rather
    /// than for the provider, so that correcting a path abandons the tiles the old one wrote
    /// instead of serving them for ever.
    /// </remarks>
    public static TileProvider GoesInfrared(string userAgent) => new(
        "goes-ir-conus",
        "https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/goes_east_conus_ch13/{z}/{x}/{y}.png",
        userAgent);
}
