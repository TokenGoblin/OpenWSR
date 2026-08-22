using System.Collections;
using OpenWSR.Nexrad;
using OpenWSR.Render.Radar;

namespace OpenWSR.App.Tests;

/// <summary>
/// The encoding the GPU filters the field through.
///
/// It is premultiplied on purpose: the value plane carries value×valid and the mask plane
/// carries the weights, so a bilinear tap or a mip level reduces to a mean over the gates
/// that measured something. The old single sentinel-encoded plane could not be filtered at
/// all — there is no sentinel value that averages correctly with real data, which is why a
/// point sample was the only safe thing to do with it.
///
/// None of this is visible in a screenshot of a working map, and a mistake here shows up as
/// a subtly wrong field rather than an error, so it is asserted directly.
/// </summary>
public sealed class SweepGeometryTests
{
    private static Sweep Sweep(float[] data, bool[] rangeFolded, int radials, int gates)
    {
        var azimuths = new float[radials];
        for (int i = 0; i < radials; i++) azimuths[i] = i * 360f / radials;

        var folded = new BitArray(radials * gates);
        for (int i = 0; i < rangeFolded.Length; i++) folded[i] = rangeFolded[i];

        return new Sweep(
            "KTLX", new DateTime(2013, 5, 20, 20, 16, 0, DateTimeKind.Utc),
            35.3331, -97.2778, 380, 2, 0.5f, Moment.Reflectivity,
            azimuths, 2125f, 250f, gates, data,
            new ScaleInfo(2f, 129f, 8), folded);
    }

    [Fact]
    public void AMeasuredGateCarriesItsValueAndFullWeight()
    {
        var g = SweepGeometry.Build(Sweep([42.5f], [false], radials: 1, gates: 1));

        Assert.Equal(42.5f, g.Values[0]);
        Assert.Equal(255, g.Mask[0]);   // valid
        Assert.Equal(0, g.Mask[1]);     // not range folded
    }

    [Fact]
    public void AnUnmeasuredGateCarriesZeroAndNoWeight()
    {
        // Zero rather than a sentinel: it is about to be summed. Zero weight is what keeps
        // it out of the mean; the zero value is what keeps it from dragging the sum down.
        var g = SweepGeometry.Build(Sweep([float.NaN], [false], radials: 1, gates: 1));

        Assert.Equal(0f, g.Values[0]);
        Assert.Equal(0, g.Mask[0]);
        Assert.Equal(0, g.Mask[1]);
    }

    [Fact]
    public void ARangeFoldedGateIsMarkedSeparatelyFromAnEmptyOne()
    {
        var g = SweepGeometry.Build(Sweep([float.NaN], [true], radials: 1, gates: 1));

        Assert.Equal(0f, g.Values[0]);
        Assert.Equal(0, g.Mask[0]);     // it measured nothing usable...
        Assert.Equal(255, g.Mask[1]);   // ...but for a reason the display shows
    }

    [Fact]
    public void FilteringTheTwoPlanesGivesTheMeanOfTheGatesThatMeasured()
    {
        // Four gates, half of them empty. This is the property the whole encoding exists
        // for: the hardware sums both planes and divides, and must land on 30 — the mean of
        // the two real gates — not 15, the mean including the holes.
        var g = SweepGeometry.Build(
            Sweep([20f, float.NaN, 40f, float.NaN], [false, false, false, false],
                  radials: 1, gates: 4));

        float valueSum = 0f, weightSum = 0f;
        for (int i = 0; i < 4; i++)
        {
            valueSum += g.Values[i];
            weightSum += g.Mask[i * 2] / 255f;
        }

        Assert.Equal(0.5f, weightSum / 4f);          // coverage: half the footprint
        Assert.Equal(30f, valueSum / weightSum);     // and the mean of what was there
    }

    [Fact]
    public void RadialsAreSortedByAzimuthAndTheMaskFollowsThem()
    {
        // Build sorts radials into azimuth order, so the mask has to be written in the same
        // order as the values or the two planes describe different gates.
        var data = new[] { 10f, float.NaN, 30f, 40f };
        var g = SweepGeometry.Build(Sweep(data, [false, false, false, false],
                                          radials: 2, gates: 2));

        for (int i = 0; i < 4; i++)
        {
            bool valid = g.Mask[i * 2] == 255;
            Assert.Equal(valid, g.Values[i] != 0f);
        }
        Assert.Equal(3, g.Mask.Where((_, i) => i % 2 == 0 && g.Mask[i] == 255).Count());
    }
}
