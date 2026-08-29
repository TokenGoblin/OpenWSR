// Derives the gamma table behind TileToning.InvertedDarkGamma: what the curve does to the
// committed OSM fixture tile, and how that compares to the CARTO tiles the dark basemap used
// to fetch.
//
// Copy into tests/OpenWSR.App.Tests/ and run:
//   dotnet test tests/OpenWSR.App.Tests \
//       --filter "FullyQualifiedName~TileToningMeasurement"
//
// Two things about the reference half. CARTO began requiring an API key in August 2026, so its
// tiles cannot be re-fetched and are not redistributable here either -- point OWSR_CARTO_DIR at
// a pre-watermark cache (%LOCALAPPDATA%\OpenWSR\tiles\carto-dark) if you still have one, and
// the reference column is skipped if you do not. And the comparison is looser than it looks:
// CARTO's own per-tile mean ranges over 11 levels across twelve adjacent tiles, which is why
// the shipped constant is set by the ground and label anchors rather than by matching a mean.
//
// Recorded result, 2026-08-27, the twelve committed z9 tiles over northern Utah
// (x 94-97, y 190-192):
//
//   CARTO dark_nolabels, per tile   9.1 .. 20.5, mean 12.2
//   OSM as published                             226.21
//
//   gamma   mosaic mean   land #f2efe9   label #333333
//   1.00          28.79             16             204   (inversion alone)
//   1.20          19.43              9             195
//   1.30          16.23              7             191
//   1.35          14.70              6             189   <- shipped
//   1.40          13.32              5             187
//   1.50          11.33              4             182

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenWSR.Render.Tiles;

namespace OpenWSR.App.Tests;

public sealed class TileToningMeasurement
{
    [Fact]
    public void Measure()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "OpenWSR.slnx")))
            repo = repo.Parent!;

        var tiles = new List<byte[]>();
        for (int x = 94; x <= 97; x++)
            for (int y = 190; y <= 192; y++)
                tiles.Add(Load(Path.Combine(repo!.FullName, "assets", "testdata", "tiles",
                    $"osm-z9-x{x}-y{y}.png")));

        var lines = new List<string>
        {
            $"OSM as published over {tiles.Count} tiles: {MeanLuminance(tiles):F2}",
        };

        if (Environment.GetEnvironmentVariable("OWSR_CARTO_DIR") is { } carto)
        {
            var means = new List<double>();
            for (int x = 94; x <= 97; x++)
                for (int y = 190; y <= 192; y++)
                {
                    var path = Path.Combine(carto, "9", x.ToString(), $"{y}.png");
                    if (File.Exists(path)) means.Add(MeanLuminance(Load(path)));
                }
            lines.Add(means.Count == 0
                ? "CARTO reference: no tiles found under OWSR_CARTO_DIR"
                : $"CARTO reference over {means.Count} tiles: {means.Min():F1} .. " +
                  $"{means.Max():F1}, mean {means.Average():F1}");
        }
        else
        {
            lines.Add("CARTO reference: set OWSR_CARTO_DIR to a pre-watermark tile cache");
        }

        lines.Add("");
        lines.Add("gamma   mosaic mean   land   label");
        foreach (double gamma in new[] { 1.00, 1.20, 1.30, 1.35, 1.40, 1.50 })
        {
            var toned = tiles.Select(t => { var c = (byte[])t.Clone(); ApplyGamma(c, gamma); return c; }).ToList();
            lines.Add($"{gamma:F2}    {MeanLuminance(toned),11:F2}   "
                      + $"{Curve(Luminance(242, 239, 233), gamma),4}   "
                      + $"{Curve(Luminance(51, 51, 51), gamma),5}");
        }

        File.WriteAllLines(Environment.GetEnvironmentVariable("OWSR_OUT")!, lines);
    }

    // Deliberately a second implementation of the curve rather than a call into TileToning: a
    // harness that reproduces a constant by asking the code for it proves nothing.
    private static byte Curve(int luminance, double gamma) =>
        (byte)Math.Round(255.0 * Math.Pow((255 - luminance) / 255.0, gamma));

    private static int Luminance(int r, int g, int b) => (29 * b + 150 * g + 77 * r) >> 8;

    private static void ApplyGamma(byte[] bgra, double gamma)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte toned = Curve(Luminance(bgra[i + 2], bgra[i + 1], bgra[i]), gamma);
            bgra[i] = bgra[i + 1] = bgra[i + 2] = toned;
        }
    }

    private static double MeanLuminance(byte[] bgra) => MeanLuminance([bgra]);

    private static double MeanLuminance(IEnumerable<byte[]> tiles)
    {
        long total = 0, count = 0;
        foreach (var bgra in tiles)
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                total += Luminance(bgra[i + 2], bgra[i + 1], bgra[i]);
                count++;
            }
        return total / (double)count;
    }

    private static byte[] Load(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(
            stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        BitmapSource source = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }
}
