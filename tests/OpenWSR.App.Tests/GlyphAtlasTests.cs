using OpenWSR.Render;

namespace OpenWSR.App.Tests;

/// <summary>
/// The glyph atlas is a grid of cells sampled by UV rectangle, so ink that strays outside its
/// own cell is drawn as part of whichever character sits next to it in the charset.
///
/// <para>That is not hypothetical: Consolas has no warning sign, so font fallback returned
/// <c>⚠</c> wider than the 14 px cell, centring spilled it into both neighbours, and the degree
/// sign — immediately to its left — carried a shard of a triangle onto every temperature the
/// station layer drew.</para>
/// </summary>
public class GlyphAtlasTests
{
    /// <summary>
    /// Runs on an STA thread: the atlas is drawn with WPF, which needs one.
    /// </summary>
    private static GlyphAtlas Build()
    {
        GlyphAtlas? atlas = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { atlas = new GlyphAtlas(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw failure;
        return atlas!;
    }

    /// <summary>
    /// No glyph may put ink in the outermost column of its cell. Anything there is sampled by
    /// the neighbouring character's UV rectangle as well.
    /// </summary>
    [Fact]
    public void NoGlyphReachesTheEdgeOfItsCell()
    {
        var atlas = Build();
        var offenders = new List<string>();

        for (int i = 0; i < GlyphAtlas.Charset.Length; i++)
        {
            int cellX = i % GlyphAtlas.Columns * GlyphAtlas.CellWidth;
            int cellY = i / GlyphAtlas.Columns * GlyphAtlas.CellHeight;

            for (int y = cellY; y < cellY + GlyphAtlas.CellHeight; y++)
            {
                if (Opaque(atlas, cellX, y) || Opaque(atlas, cellX + GlyphAtlas.CellWidth - 1, y))
                {
                    offenders.Add($"'{GlyphAtlas.Charset[i]}' (index {i})");
                    break;
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The root cause, named: <c>⚠</c> is not in Consolas, so it arrives from font fallback at
    /// that face's width, and unshrunk it is wider than the cell. Asserted on the glyph that
    /// caused it rather than on the one that showed it — the degree sign's own ring reaches
    /// most of the way across its cell, so "is there ink on the right" cannot tell the two
    /// apart, while "does the warning sign fit" can.
    /// </summary>
    [Fact]
    public void TheWarningSignIsShrunkToFitItsCell()
    {
        int warning = GlyphAtlas.Charset.IndexOf('⚠');
        Assert.True(warning >= 0);

        // The premise of the bug: it sits immediately after the degree sign, on the same row,
        // so anything it spills to its left lands on a temperature label.
        Assert.Equal('°', GlyphAtlas.Charset[warning - 1]);
        Assert.Equal(warning / GlyphAtlas.Columns, (warning - 1) / GlyphAtlas.Columns);

        var atlas = Build();
        var (left, right) = InkColumns(atlas, warning);

        Assert.True(left >= 1, $"warning sign starts at column {left}, touching the cell edge");
        Assert.True(right <= GlyphAtlas.CellWidth - 2,
            $"warning sign ends at column {right} of {GlyphAtlas.CellWidth}");
    }

    /// <summary>Leftmost and rightmost columns holding ink, relative to the cell.</summary>
    private static (int Left, int Right) InkColumns(GlyphAtlas atlas, int index)
    {
        int cellX = index % GlyphAtlas.Columns * GlyphAtlas.CellWidth;
        int cellY = index / GlyphAtlas.Columns * GlyphAtlas.CellHeight;
        int left = GlyphAtlas.CellWidth, right = -1;

        for (int y = cellY; y < cellY + GlyphAtlas.CellHeight; y++)
            for (int x = 0; x < GlyphAtlas.CellWidth; x++)
                if (Opaque(atlas, cellX + x, y))
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
        return (left, right);
    }

    /// <summary>Every character still draws something — shrinking must not erase one.</summary>
    [Fact]
    public void EveryCharacterExceptSpaceHasInk()
    {
        var atlas = Build();

        for (int i = 0; i < GlyphAtlas.Charset.Length; i++)
        {
            if (GlyphAtlas.Charset[i] == ' ') continue;

            int cellX = i % GlyphAtlas.Columns * GlyphAtlas.CellWidth;
            int cellY = i / GlyphAtlas.Columns * GlyphAtlas.CellHeight;
            bool any = false;

            for (int y = cellY; y < cellY + GlyphAtlas.CellHeight && !any; y++)
                for (int x = cellX; x < cellX + GlyphAtlas.CellWidth && !any; x++)
                    any = Opaque(atlas, x, y);

            Assert.True(any, $"'{GlyphAtlas.Charset[i]}' drew nothing");
        }
    }

    /// <summary>Alpha above a threshold that ignores antialiasing's faintest fringe.</summary>
    private static bool Opaque(GlyphAtlas atlas, int x, int y) =>
        atlas.Bgra[(y * atlas.TextureWidth + x) * 4 + 3] > 24;
}
