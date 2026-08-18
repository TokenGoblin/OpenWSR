namespace OpenWSR.Geo;

/// <summary>Standard XYZ (slippy map) tile scheme over Web Mercator.</summary>
public readonly record struct TileKey(int Z, int X, int Y)
{
    public bool IsValid => Z >= 0 && X >= 0 && Y >= 0 && X < (1 << Z) && Y < (1 << Z);
    public TileKey Parent => new(Z - 1, X >> 1, Y >> 1);
    public override string ToString() => $"{Z}/{X}/{Y}";
}

public static class TileMath
{
    public const int TilePixels = 256;
    public const double WorldSizeM = 2.0 * GeoMath.MercatorExtentM;

    public static double TileSizeM(int z) => WorldSizeM / (1 << z);

    /// <summary>Integer zoom whose tiles are rendered at &gt;= 1:1 pixel scale for the given resolution.</summary>
    public static int ZoomForMetersPerPixel(double metersPerPixel, int minZoom = 0, int maxZoom = 19)
    {
        double z = Math.Log2(WorldSizeM / (metersPerPixel * TilePixels));
        return Math.Clamp((int)Math.Round(z), minZoom, maxZoom);
    }

    /// <summary>Tile containing a Web Mercator point at the given zoom.</summary>
    public static TileKey TileAt(double mercX, double mercY, int z)
    {
        double size = TileSizeM(z);
        int x = (int)Math.Floor((mercX + GeoMath.MercatorExtentM) / size);
        int y = (int)Math.Floor((GeoMath.MercatorExtentM - mercY) / size);
        int n = 1 << z;
        return new TileKey(z, Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
    }

    /// <summary>Mercator bounds of a tile: (MinX, MinY, MaxX, MaxY).</summary>
    public static (double MinX, double MinY, double MaxX, double MaxY) Bounds(TileKey key)
    {
        double size = TileSizeM(key.Z);
        double minX = -GeoMath.MercatorExtentM + key.X * size;
        double maxY = GeoMath.MercatorExtentM - key.Y * size;
        return (minX, maxY - size, minX + size, maxY);
    }

    /// <summary>All tiles intersecting a Mercator rectangle at the given zoom.</summary>
    public static IEnumerable<TileKey> Cover(
        double minX, double minY, double maxX, double maxY, int z)
    {
        int n = 1 << z;
        var a = TileAt(minX, maxY, z); // top-left
        var b = TileAt(maxX, minY, z); // bottom-right
        for (int y = a.Y; y <= b.Y && y < n; y++)
            for (int x = a.X; x <= b.X && x < n; x++)
                yield return new TileKey(z, x, y);
    }
}
