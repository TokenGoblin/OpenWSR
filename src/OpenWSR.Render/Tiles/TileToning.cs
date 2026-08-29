namespace OpenWSR.Render.Tiles;

/// <summary>How a tile's pixels are transformed between decoding and drawing.</summary>
public enum TileTone
{
    /// <summary>Draw the tiles as the service published them.</summary>
    AsPublished,

    /// <summary>
    /// Turn a light street map into a dark one: desaturate, invert, then pull the midtones
    /// down. See <see cref="TileToning"/> for why this exists and how the curve was chosen.
    /// </summary>
    InvertedDark,
}

/// <summary>
/// Derives a dark basemap from a light one at decode time.
/// </summary>
/// <remarks>
/// <para>This exists because the free keyless dark basemap ran out. CARTO's "Dark Matter"
/// was the ground this application's colour design rests on — reflectivity is a bright,
/// saturated palette and needs to be the only bright thing on screen — and in August 2026
/// CARTO began requiring an API key, serving every unkeyed request an otherwise valid PNG
/// with "API KEY REQUIRED" stamped across it. They are retiring the raster basemaps
/// besides. Esri's Dark Gray Canvas, the one remaining keyless dark style, is not a
/// substitute: over the same twelve tiles it measures a mid-grey 66.7 against CARTO's 12.2,
/// and its land/water polarity is inverted, so lakes read as holes punched in a grey
/// field.</para>
///
/// <para><b>Inverting, not darkening.</b> Multiplying a light map down is the obvious move and
/// the wrong one: OSM's ground is near-white and its detail sits <em>above</em> that, so
/// scaling flattens everything toward one grey and the map loses its structure. Inverting
/// maps the near-white ground to near-black and OSM's near-black label text to white, which
/// is exactly the arrangement the dark style had. Desaturating first is the same argument as
/// the basemap choice itself — on a colour-inverted map, OSM's green landcover and blue water
/// become magenta and orange, and the palette then has company.</para>
///
/// <para><b>Where the curve comes from.</b> The criterion is the ground and the labels, not a
/// number: OSM's land <c>#f2efe9</c> has to land near-black and its label text has to stay
/// legible over an echo. At 1.35 those are 6 and 189 on a 0-255 scale. Mean luminance is the
/// cross-check rather than the target, and it is worth knowing how loose a check it is — over
/// twelve z9 tiles across northern Utah, CARTO's own per-tile mean ranges from 9.1 to 20.5,
/// averaging 12.2, because the two styles disagree about how much landcover to paint. Against
/// that, inversion alone gives 28.8 and 1.35 gives 14.7; anything from about 1.3 to 1.5 sits
/// inside CARTO's own spread. So do not tune this to a decimal place, and do not tune it by
/// eye either — check the two anchors, and use
/// <c>resources/measurements/TileToningMeasurement.cs</c> to reproduce the table.</para>
///
/// <para><b>What is lost.</b> OSM draws minor roads white, which inverts to black and takes
/// them out of a dark map entirely. Motorways and boundaries survive, being drawn in colour.
/// This is a real reduction against the dark style and an acceptable one at radar zooms,
/// where the road network is context rather than the subject. Place names are the other
/// cost: OSM bakes them into the tile, so they draw <em>under</em> the weather rather than
/// above it as CARTO's separate label layer allowed.</para>
/// </remarks>
public static class TileToning
{
    /// <summary>
    /// Applied to the inverted luminance. Set by where it puts OSM's ground and its label
    /// text, with mean luminance against the CARTO tiles this replaces as a cross-check — see
    /// the class remarks. Not a taste setting.
    /// </summary>
    private const double InvertedDarkGamma = 1.35;

    private static readonly byte[] InvertedDarkLut = BuildInvertedDarkLut();

    /// <summary>Transform a decoded BGRA tile in place. Alpha is untouched.</summary>
    public static void Apply(Span<byte> bgra, TileTone tone)
    {
        if (tone == TileTone.AsPublished) return;

        var lut = InvertedDarkLut;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            // Rec. 601 luminance in fixed point; the weights sum to 256, so the shift cannot
            // overflow a byte and no clamp is needed.
            int luminance = (29 * bgra[i] + 150 * bgra[i + 1] + 77 * bgra[i + 2]) >> 8;
            byte toned = lut[luminance];
            bgra[i] = toned;
            bgra[i + 1] = toned;
            bgra[i + 2] = toned;
        }
    }

    /// <summary>The transfer curve, exposed so a test can assert its shape rather than its
    /// effect on one fixture tile.</summary>
    public static byte Map(TileTone tone, byte luminance) =>
        tone == TileTone.AsPublished ? luminance : InvertedDarkLut[luminance];

    private static byte[] BuildInvertedDarkLut()
    {
        var lut = new byte[256];
        for (int i = 0; i < lut.Length; i++)
            lut[i] = (byte)Math.Round(255.0 * Math.Pow((255 - i) / 255.0, InvertedDarkGamma));
        return lut;
    }
}
