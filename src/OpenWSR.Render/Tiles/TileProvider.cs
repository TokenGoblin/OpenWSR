using OpenWSR.Geo;

namespace OpenWSR.Render.Tiles;

/// <summary>An XYZ raster tile source. Provider and key are user-configurable from day one.</summary>
public sealed record TileProvider(string Name, string UrlTemplate, string UserAgent)
{
    /// <summary>
    /// A transparent place-name layer to draw <em>above</em> the weather, when this style
    /// publishes one separately.
    /// </summary>
    /// <remarks>
    /// It belongs on the provider rather than in the renderer because it is a property of
    /// the style: only some publish labels apart from the ground, and the ones that bake
    /// them in must not get a second copy of every name drawn over the first.
    /// </remarks>
    public TileProvider? Labels { get; init; }

    /// <summary>
    /// Tint applied to <see cref="Labels"/>. Above one lifts toward white and clamps, which
    /// is what a mid-grey label layer needs over a near-black ground; a dark label layer on
    /// a light ground wants one.
    /// </summary>
    public float LabelBoost { get; init; } = 1f;

    /// <summary>
    /// A transform applied to every tile between decode and draw. It is a property of the
    /// style rather than of the source, which is why <see cref="OsmDark"/> can share both
    /// the disk cache and the attribution of <see cref="Osm"/>.
    /// </summary>
    public TileTone Tone { get; init; } = TileTone.AsPublished;

    public string UrlFor(TileKey key) => UrlTemplate
        .Replace("{z}", key.Z.ToString())
        .Replace("{x}", key.X.ToString())
        .Replace("{y}", key.Y.ToString());

    public static TileProvider Osm(string userAgent) => new(
        "osm", "https://tile.openstreetmap.org/{z}/{x}/{y}.png", userAgent);

    /// <summary>
    /// The dark basemap: OpenStreetMap's own tiles, inverted to a dark ground as they decode.
    /// </summary>
    /// <remarks>
    /// <para>The point is contrast. Reflectivity is a bright, saturated palette, and on the
    /// standard OSM style it competes with green landcover, blue water and orange roads for the
    /// same part of the eye. Against a near-black ground the weather is the only bright thing on
    /// screen, which is why every broadcast and consumer radar app looks like this.</para>
    ///
    /// <para>This used to be CARTO's "Dark Matter", which was free and keyless until August 2026
    /// and is now neither — unkeyed requests come back as a valid PNG reading "API KEY REQUIRED",
    /// and the raster basemaps are being retired outright. Deriving the dark style here instead
    /// of fetching one removes the dependency rather than moving it: OSM's tiles are already
    /// fetched for the light option, and a style nobody can gate cannot be gated. See
    /// <see cref="TileToning"/> for the transform and what it costs.</para>
    ///
    /// <para>It carries <see cref="Osm"/>'s name deliberately. The disk cache holds the bytes as
    /// downloaded and the toning happens on the way to the texture, so the two styles are the
    /// same tiles and should share one cache; the attribution is the same for the same reason.</para>
    /// </remarks>
    public static TileProvider OsmDark(string userAgent) =>
        Osm(userAgent) with { Tone = TileTone.InvertedDark };

    /// <summary>
    /// USGS topographic maps — the terrain option.
    /// </summary>
    /// <remarks>
    /// Public domain, being a work of the US government, and needs no key. US-only, which is
    /// the coverage this application has anyway.
    ///
    /// <para><b>Not <c>USGSShadedReliefOnly</c>, which would suit the radar better.</b> Grey
    /// relief would have left the reflectivity palette the only saturated thing on screen,
    /// the same argument that makes the default dark. But that service's tile cache has holes
    /// in it: over northern Utah it serves z8 and z12 and returns 404 at z9 and z10, despite
    /// its own metadata advertising levels 0 to 23. A basemap that vanishes at two zooms in
    /// the middle of the range is not a basemap. <c>USGSTopo</c> was complete at every level
    /// checked.</para>
    ///
    /// <para>It bakes its own names in, so it gets no <see cref="Labels"/> layer: a second
    /// copy would draw every place name twice, once shifted.</para>
    ///
    /// <para>Note the tile path is <c>{z}/{y}/{x}</c> — ArcGIS orders row before column where
    /// XYZ orders column before row. Swapping them still returns tiles, just the wrong ones.
    /// Verified by fetching a Rockies tile and a Pacific one: in the right order the mountain
    /// tile is 20 kB of texture and the ocean 2.4 kB of nothing, and in the wrong order the
    /// mountain tile comes back byte-identical to blank ocean.</para>
    /// </remarks>
    public static TileProvider UsgsTopo(string userAgent) => new(
        "usgs-topo",
        "https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/tile/{z}/{y}/{x}",
        userAgent);

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
