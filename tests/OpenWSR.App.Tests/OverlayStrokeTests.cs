using OpenWSR.Render;

namespace OpenWSR.App.Tests;

/// <summary>
/// Strokes are drawn one segment at a time, and a segment rounds its far end only. That makes
/// a chain join correctly with each cap drawn once — but only if the shapes really are chains,
/// so these assert the property the cap rule depends on.
/// </summary>
public sealed class OverlayStrokeTests
{
    [Fact]
    public void TheOverlayShadersCompile() => OverlayRenderer.ValidateShaders();

    [Fact]
    public void ATupleStillMakesALineAndItIsChained()
    {
        var geometry = new OverlayGeometry();
        geometry.Lines.Add((0, 0, 100, 0, 0xFFFFFFFF, 2f));
        Assert.Equal(LineCaps.Joined, geometry.Lines[0].Caps);
    }

    [Fact]
    public void ACircleIsAClosedChain()
    {
        var geometry = new OverlayGeometry();
        StormOverlayController.AddCircle(geometry, (1000, 2000), 500, 0xFFFFFFFF, 3f);

        Assert.NotEmpty(geometry.Lines);
        for (int i = 0; i < geometry.Lines.Count; i++)
        {
            var line = geometry.Lines[i];
            var next = geometry.Lines[(i + 1) % geometry.Lines.Count];
            // Each segment ends exactly where the next begins, so the round end that lands on
            // the shared vertex covers the square start the next one puts there.
            Assert.Equal(line.Bx, next.Ax, 9);
            Assert.Equal(line.By, next.Ay, 9);
        }
    }

    [Fact]
    public void ACircleIsSmoothEnoughToReadAsOne()
    {
        var geometry = new OverlayGeometry();
        const double radius = 500;
        StormOverlayController.AddCircle(geometry, (0, 0), radius, 0xFFFFFFFF, 3f);

        // The chord sags below the true arc by r(1 - cos(pi / n)). Held under half a pixel on
        // a ring 500 px across, the polygon is finer than the shader's one-pixel edge ramp.
        double sag = radius * (1 - Math.Cos(Math.PI / geometry.Lines.Count));
        Assert.True(sag < 0.5, $"{geometry.Lines.Count} sides sags {sag:F2} px");
    }

    [Fact]
    public void APolygonOutlineIsAClosedChain()
    {
        var geometry = new OverlayGeometry();
        geometry.AddPolygonOutline([(0, 0), (100, 0), (100, 100), (0, 100)], 0xFFFFFFFF, 2f);

        Assert.Equal(4, geometry.Lines.Count);
        for (int i = 0; i < geometry.Lines.Count; i++)
        {
            var next = geometry.Lines[(i + 1) % geometry.Lines.Count];
            Assert.Equal(geometry.Lines[i].Bx, next.Ax, 9);
            Assert.Equal(geometry.Lines[i].By, next.Ay, 9);
        }
    }
}
