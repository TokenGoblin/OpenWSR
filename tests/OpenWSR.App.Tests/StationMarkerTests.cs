using OpenWSR.Geo;
using OpenWSR.Ingest;

namespace OpenWSR.App.Tests;

/// <summary>
/// The map's station layer. Only the geometry is asserted here — the fetching is three network
/// clients already covered by their own suites — but the geometry is where the traps are:
/// markers are sized in screen pixels, and a symbol that forgets to scale is sub-pixel at
/// national zoom and screen-filling at street zoom.
/// </summary>
[Collection("units")]
public class StationMarkerTests : IDisposable
{
    private readonly UnitSystem _original = Units.System;

    public void Dispose() => Units.System = _original;

    private static CurrentConditions Reading(
        double latDeg, double lonDeg, double? celsius, StationSource source, string id = "KX") =>
        new(new ObservationStation(id, id, latDeg, lonDeg, 0, 0) { Source = source },
            DateTimeOffset.UtcNow, "", "", celsius,
            null, null, null, null, null, null, null, null);

    /// <summary>
    /// The whole reason overlay symbols carry a metres-per-pixel factor: a marker fixed in
    /// Mercator metres is invisible at one zoom and covers the state at another.
    /// </summary>
    [Fact]
    public void MarkersScaleWithTheZoom()
    {
        var readings = new[] { Reading(35.2, -97.4, 20, StationSource.Nws) };

        var near = StationMarkers.Build(readings, metresPerPixel: 10).Geometry;
        var far = StationMarkers.Build(readings, metresPerPixel: 1000).Geometry;

        Assert.Equal(100.0, Extent(far) / Extent(near), 6);
    }

    [Fact]
    public void EachStationGetsAMarkAndItsTemperature()
    {
        Units.System = UnitSystem.Imperial;
        var readings = new[]
        {
            Reading(35.2, -97.4, 20, StationSource.Nws, "KOUN"),
            Reading(35.3, -97.5, 25, StationSource.Personal, "KOKONE"),
        };

        var (geometry, labels) = StationMarkers.Build(readings, 100);

        Assert.Equal(2, labels.Count);
        Assert.Contains(labels, l => l.Text == "68°");   // 20 °C
        Assert.Contains(labels, l => l.Text == "77°");   // 25 °C
        Assert.NotEmpty(geometry.FillTriangles);
        Assert.NotEmpty(geometry.Lines);

        // Anchored on the station and offset clear of the dot, so the number does not sit on
        // the mark it belongs to.
        var (x, y) = GeoMath.ToMercator(35.2, -97.4);
        var label = labels.Single(l => l.Text == "68°");
        Assert.Equal(x, label.MercX, 6);
        Assert.Equal(y, label.MercY, 6);
        Assert.True(label.OffsetXPx > 0);
    }

    /// <summary>
    /// A station reporting no temperature still gets a mark — it is a real instrument at a real
    /// place, and the map showing where the network is thin is worth something — but it must
    /// not get a label, since there is no number to write.
    /// </summary>
    [Fact]
    public void AStationWithNoTemperatureIsMarkedButNotLabelled()
    {
        var readings = new[] { Reading(35.2, -97.4, null, StationSource.Nws) };
        var (geometry, labels) = StationMarkers.Build(readings, 100);

        Assert.Empty(labels);
        Assert.NotEmpty(geometry.FillTriangles);
    }

    /// <summary>
    /// Official first, then a neighbour's, then your own — so where two sit close together, and
    /// near a house they routinely do, the one you would act on is the one on top.
    /// </summary>
    [Fact]
    public void YourOwnStationIsDrawnLast()
    {
        Units.System = UnitSystem.Metric;

        // Deliberately supplied worst-first, so passing means the builder sorted rather than
        // that the input happened to be in the right order.
        var readings = new[]
        {
            Reading(35.2, -97.4, 20, StationSource.Own, "MINE"),
            Reading(35.2, -97.4, 21, StationSource.Nws, "KOUN"),
            Reading(35.2, -97.4, 22, StationSource.Personal, "KOKONE"),
        };

        // Labels come out in draw order, so the last is the one on top.
        var order = StationMarkers.Build(readings, 100).Labels.Select(l => l.Text).ToList();

        Assert.Equal(["21°", "22°", "20°"], order);
    }

    [Fact]
    public void NoStationsMeansNoGeometry()
    {
        var (geometry, labels) = StationMarkers.Build([], 100);

        Assert.Empty(labels);
        Assert.Empty(geometry.FillTriangles);
        Assert.Empty(geometry.Lines);
    }

    /// <summary>Width of everything drawn, in Mercator metres.</summary>
    private static double Extent(OpenWSR.Render.OverlayGeometry g)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var (x, _, _) in g.FillTriangles)
        {
            min = Math.Min(min, x);
            max = Math.Max(max, x);
        }
        return max - min;
    }
}
