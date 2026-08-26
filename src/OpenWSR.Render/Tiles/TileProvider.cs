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
        "carto-dark", "https://basemaps.cartocdn.com/dark_nolabels/{z}/{x}/{y}.png", userAgent)
    {
        Labels = CartoDarkLabels(userAgent),
        LabelBoost = 2.2f,
    };

    /// <summary>
    /// The place names from the same style, as a transparent layer of their own.
    ///
    /// Split out because labels belong <em>above</em> the weather, not under it. Baked into
    /// the basemap they are the first thing an echo covers, and the name of the town a storm
    /// is over is exactly what you want to read at that moment.
    ///
    /// They also need brightening. CARTO draws them mid-grey — the brightest pixel in a tile
    /// measures (161, 161, 161) and the mean (103, 103, 103) — which reads as dim against the
    /// near-black ground and is unreadable over a bright echo. The quad shader multiplies by
    /// its tint, so a tint above one lifts them toward white and clamps there.
    /// </summary>
    public static TileProvider CartoDarkLabels(string userAgent) => new(
        "carto-dark-labels",
        "https://basemaps.cartocdn.com/dark_only_labels/{z}/{x}/{y}.png",
        userAgent);

    /// <summary>The same layer for a light ground: dark text instead of mid-grey.</summary>
    public static TileProvider CartoLightLabels(string userAgent) => new(
        "carto-light-labels",
        "https://basemaps.cartocdn.com/light_only_labels/{z}/{x}/{y}.png",
        userAgent);

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
