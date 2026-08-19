using System.Collections;
using OpenWSR.Geo;
using OpenWSR.Nexrad;
using OpenWSR.Nexrad.Analysis;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// A track is a maximum held over time, so the questions are: does it keep the strongest
/// value rather than the latest, does a moving feature draw a line, and does the swath
/// land where the feature actually was on the ground.
/// </summary>
public class RotationTracksTests
{
    private const int Radials = 360;
    private const int Gates = 200;
    private const float FirstGateM = 2000;
    private const float GateSpacingM = 500;
    private const double RadarLat = 35.333, RadarLon = -97.278;

    private static Sweep ShearSweep(Func<int, int, float> value, DateTime time)
    {
        var data = new float[Radials * Gates];
        for (int r = 0; r < Radials; r++)
            for (int g = 0; g < Gates; g++)
                data[r * Gates + g] = value(r, g);

        return new Sweep(
            "KTLX", time, RadarLat, RadarLon, 380,
            1, 0.5f, Moment.AzimuthalShear,
            [.. Enumerable.Range(0, Radials).Select(i => i * 360f / Radials)],
            FirstGateM, GateSpacingM, Gates, data,
            new ScaleInfo(1f, 0f, 8),
            new BitArray(Radials * Gates));
    }

    /// <summary>A compact blob of shear centred on one radial and gate.</summary>
    private static Sweep BlobSweep(int radial, int gate, float peak, DateTime time) =>
        ShearSweep((r, g) =>
        {
            int dr = Math.Abs(((r - radial + 540) % 360) - 180);
            int dg = Math.Abs(g - gate);
            return dr <= 3 && dg <= 3 ? peak : 0f;
        }, time);

    /// <summary>Where a (radial, gate) sample lands in the result grid.</summary>
    private static (int Column, int Row) CellFor(RotationTrackResult t, int radial, int gate)
    {
        double azimuth = radial * 360.0 / Radials;
        double slant = FirstGateM + gate * GateSpacingM;
        // At half a degree the slant and ground ranges differ by far less than a cell.
        var (lat, lon) = GeoMath.Offset(RadarLat, RadarLon, azimuth * Math.PI / 180.0, slant);
        var (x, y) = GeoMath.ToMercator(lat, lon);
        int column = (int)((x - t.MinX) / (t.MaxX - t.MinX) * t.Width);
        int row = (int)((t.MaxY - y) / (t.MaxY - t.MinY) * t.Height);
        return (column, row);
    }

    private static float PeakNear(RotationTrackResult t, int column, int row, int half = 4)
    {
        float peak = float.NaN;
        for (int r = row - half; r <= row + half; r++)
            for (int c = column - half; c <= column + half; c++)
            {
                if (r < 0 || r >= t.Height || c < 0 || c >= t.Width) continue;
                float v = t.Values[r * t.Width + c];
                if (!float.IsNaN(v) && (float.IsNaN(peak) || v > peak)) peak = v;
            }
        return peak;
    }

    [Fact]
    public void KeepsTheStrongestValueNotTheLatest()
    {
        // The whole point of a track: a cell that rotated hard once keeps that value even
        // though later scans show it calm.
        var t = RotationTracks.Accumulate([
            BlobSweep(90, 60, 0.004f, DateTime.UnixEpoch),
            BlobSweep(90, 60, 0.018f, DateTime.UnixEpoch.AddMinutes(5)),
            BlobSweep(90, 60, 0.002f, DateTime.UnixEpoch.AddMinutes(10)),
        ], resolution: 256);

        var (column, row) = CellFor(t, 90, 60);
        Assert.Equal(0.018f, PeakNear(t, column, row), 4);
    }

    [Fact]
    public void AMovingCoupletDrawsALine()
    {
        // Three scans, the blob stepping across azimuth. The swath must show all three,
        // which no single scan does.
        var scans = new List<Sweep>();
        for (int i = 0; i < 3; i++)
            scans.Add(BlobSweep(80 + i * 12, 60, 0.015f, DateTime.UnixEpoch.AddMinutes(5 * i)));

        var t = RotationTracks.Accumulate(scans, resolution: 512);

        for (int i = 0; i < 3; i++)
        {
            var (column, row) = CellFor(t, 80 + i * 12, 60);
            Assert.True(PeakNear(t, column, row) > 0.014f,
                $"scan {i}'s position should be in the swath");
        }
    }

    [Fact]
    public void TheSwathLandsWhereTheFeatureWas()
    {
        // Geolocation: a blob due east of the radar must appear east of the radar in the
        // grid, not merely somewhere.
        var t = RotationTracks.Accumulate(
            [BlobSweep(radial: 90, gate: 100, peak: 0.02f, DateTime.UnixEpoch)], resolution: 512);

        var (radarX, radarY) = GeoMath.ToMercator(RadarLat, RadarLon);
        int radarColumn = (int)((radarX - t.MinX) / (t.MaxX - t.MinX) * t.Width);
        int radarRow = (int)((t.MaxY - radarY) / (t.MaxY - t.MinY) * t.Height);

        // Find the strongest cell in the whole swath.
        int best = 0;
        for (int i = 1; i < t.Values.Length; i++)
            if (!float.IsNaN(t.Values[i]) &&
                (float.IsNaN(t.Values[best]) || t.Values[i] > t.Values[best])) best = i;

        int peakColumn = best % t.Width, peakRow = best / t.Width;
        Assert.True(peakColumn > radarColumn, "azimuth 90 is east, so the peak must be to the right");
        Assert.True(Math.Abs(peakRow - radarRow) < t.Height / 20,
            "azimuth 90 is due east, so it should be level with the radar");
    }

    [Fact]
    public void PeakReportsTheStrongestRotationInTheSwath()
    {
        var t = RotationTracks.Accumulate([
            BlobSweep(90, 60, 0.011f, DateTime.UnixEpoch),
            BlobSweep(200, 80, 0.019f, DateTime.UnixEpoch.AddMinutes(5)),
        ], resolution: 256);

        Assert.Equal(0.019f, t.Peak, 4);
    }

    [Fact]
    public void AnticyclonicRotationDoesNotMasqueradeAsATrack()
    {
        // Signed maximum, not absolute: a strong anticyclonic couplet is real but is a
        // different question, and must not paint a cyclonic track.
        var t = RotationTracks.Accumulate(
            [BlobSweep(90, 60, -0.020f, DateTime.UnixEpoch)], resolution: 256);

        var (column, row) = CellFor(t, 90, 60);
        Assert.True(PeakNear(t, column, row) <= 0f);
        Assert.True(t.Peak <= 0f, $"peak should not be positive, was {t.Peak}");
    }

    [Fact]
    public void SpanAndCountDescribeWhatWentIn()
    {
        var start = new DateTime(2013, 5, 20, 20, 0, 0, DateTimeKind.Utc);
        var t = RotationTracks.Accumulate([
            BlobSweep(90, 60, 0.01f, start),
            BlobSweep(95, 60, 0.01f, start.AddMinutes(4)),
            BlobSweep(100, 60, 0.01f, start.AddMinutes(9)),
        ], resolution: 128);

        Assert.Equal(3, t.ScanCount);
        Assert.Equal(start, t.StartUtc);
        Assert.Equal(start.AddMinutes(9), t.EndUtc);
    }

    [Fact]
    public void AreaBeyondTheRadarRangeIsNeverSampled()
    {
        var t = RotationTracks.Accumulate(
            [BlobSweep(90, 60, 0.02f, DateTime.UnixEpoch)], resolution: 256);

        // The grid is the bounding box of a circle, so its corners are outside the reach
        // of the radar and must stay unsampled rather than being filled with zero.
        Assert.True(float.IsNaN(t.Values[0]), "top-left corner is outside radar range");
        Assert.True(float.IsNaN(t.Values[t.Width - 1]), "top-right corner is outside radar range");
        Assert.True(float.IsNaN(t.Values[^1]), "bottom-right corner is outside radar range");
    }

    [Fact]
    public void AnEmptyInputIsRejected() =>
        Assert.Throws<ArgumentException>(() => RotationTracks.Accumulate([]));
}
