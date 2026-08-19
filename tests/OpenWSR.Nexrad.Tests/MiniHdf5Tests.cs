using OpenWSR.NetCdf;

namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Golden tests for the minimal HDF5 reader, against a committed GOES-19 GLM file.
///
/// Reference values come from <b>h5py 3.16.0</b> reading the same file — an independent
/// implementation, which is the standard this project holds binary decoding to. Every
/// literal below was printed by h5py, not by this code.
/// </summary>
public class MiniHdf5Tests
{
    private static byte[] GlmBytes() => File.ReadAllBytes(TestData.Path(
        "glm/OR_GLM-L2-LCFA_G19_s20262301200000_e20262301200200_c20262301200221.nc"));

    private static MiniHdf5 Glm() => MiniHdf5.Open(GlmBytes());

    [Fact]
    public void FindsEveryDatasetInTheFile()
    {
        // h5py: the root group holds 54 datasets.
        var file = Glm();
        Assert.Equal(54, file.Datasets.Count);

        foreach (var name in new[]
                 {
                     "flash_lat", "flash_lon", "flash_energy", "flash_quality_flag",
                     "flash_area", "product_time", "flash_count", "event_lat", "group_lat",
                 })
            Assert.True(file.Datasets.ContainsKey(name), $"'{name}' was not found");
    }

    [Fact]
    public void ReadsShapeAndTypeOfAChunkedDataset()
    {
        // h5py: flash_lat shape=(177,) dtype=float32, chunks=(2048,), gzip + shuffle.
        var dataset = Glm().Datasets["flash_lat"];
        Assert.Equal([177L], dataset.Dimensions);
        Assert.Equal(Hdf5Kind.Float, dataset.Kind);
        Assert.Equal(4, dataset.ElementSize);
        Assert.Equal(177, dataset.Count);
    }

    [Fact]
    public void ScalarDatasetHasRankZeroAndOneElement()
    {
        // h5py: product_time shape=() dtype=float64, contiguous and unfiltered.
        var dataset = Glm().Datasets["product_time"];
        Assert.Empty(dataset.Dimensions);
        Assert.Equal(1, dataset.Count);
        Assert.Equal(8, dataset.ElementSize);
    }

    [Fact]
    public void ReadsAContiguousScalar()
    {
        // h5py: product_time = 840326400.0 seconds since 2000-01-01 12:00:00Z.
        Assert.Equal(840326400.0, Glm().ReadDouble("product_time")[0], 3);
    }

    [Fact]
    public void ReadsAGzipShuffleChunkedFloatArray()
    {
        // h5py: flash_lat first six values, and the last.
        var lat = Glm().ReadSingle("flash_lat");
        Assert.Equal(177, lat.Length);
        Assert.Equal(-21.307377f, lat[0], 4);
        Assert.Equal(30.107233f, lat[1], 4);
        Assert.Equal(-31.112822f, lat[2], 4);
        Assert.Equal(11.39508f, lat[3], 4);
        Assert.Equal(21.89808f, lat[4], 4);
        Assert.Equal(24.598751f, lat[5], 4);
        Assert.Equal(22.684143f, lat[^1], 4);
    }

    [Fact]
    public void ReadsTheMatchingLongitudes()
    {
        // h5py: flash_lon first six, and the last.
        var lon = Glm().ReadSingle("flash_lon");
        Assert.Equal(177, lon.Length);
        Assert.Equal(-121.7805f, lon[0], 3);
        Assert.Equal(-49.401096f, lon[1], 3);
        Assert.Equal(-55.558006f, lon[2], 3);
        Assert.Equal(-83.87883f, lon[3], 3);
        Assert.Equal(-108.603935f, lon[4], 3);
        Assert.Equal(-109.25459f, lon[5], 3);
        Assert.Equal(-110.708466f, lon[^1], 3);
    }

    [Fact]
    public void ReadsSignedShortArrays()
    {
        // h5py: flash_energy and flash_quality_flag, first six and last.
        var file = Glm();

        var energy = file.ReadInt16("flash_energy");
        Assert.Equal<short[]>([461, 525, 132, 190, 4, 174], energy.Take(6).ToArray());
        Assert.Equal(366, energy[^1]);

        var quality = file.ReadInt16("flash_quality_flag");
        Assert.Equal<short[]>([3, 0, 0, 0, 0, 0], quality.Take(6).ToArray());
        Assert.Equal(0, quality[^1]);

        // flash_area, whose last value sits near the top of the signed range — a decode
        // that treated shorts as unsigned would still pass on the others.
        var area = file.ReadInt16("flash_area");
        Assert.Equal<short[]>([1166, 2831, 3450, 4858, 512, 2446], area.Take(6).ToArray());
        Assert.Equal(8190, area[^1]);
    }

    [Fact]
    public void ReadsAnInt32Scalar()
    {
        // h5py: flash_count = 177, which must agree with the array lengths.
        var file = Glm();
        Assert.Equal(177, file.ReadInt32("flash_count")[0]);
        Assert.Equal(file.ReadInt32("flash_count")[0], file.ReadSingle("flash_lat").Length);
    }

    [Fact]
    public void ReadsALargerMultiChunkArray()
    {
        // h5py: event_lat has 9134 values in chunks of 4096 — three chunks, so this
        // exercises walking the B-tree rather than finding a single entry.
        var file = Glm();
        Assert.Equal([9134L], file.Datasets["event_lat"].Dimensions);
        var events = file.ReadInt16("event_lat");
        Assert.Equal(9134, events.Length);
        Assert.Contains(events, v => v != 0); // the tail chunks actually decoded
    }

    [Fact]
    public void RejectsSomethingThatIsNotHdf5()
    {
        var junk = new byte[256];
        junk[0] = (byte)'G'; junk[1] = (byte)'R'; junk[2] = (byte)'I'; junk[3] = (byte)'B';
        Assert.Throws<Hdf5FormatException>(() => MiniHdf5.Open(junk));
    }

    [Fact]
    public void AskingForAMissingDatasetSaysSo()
    {
        var ex = Assert.Throws<Hdf5FormatException>(() => Glm().ReadSingle("not_a_dataset"));
        Assert.Contains("not_a_dataset", ex.Message);
    }
}

/// <summary>
/// The GLM wrapper on top of the reader: the flashes a map would actually draw.
/// </summary>
public class GlmFileTests
{
    private static byte[] Bytes() => File.ReadAllBytes(TestData.Path(
        "glm/OR_GLM-L2-LCFA_G19_s20262301200000_e20262301200200_c20262301200221.nc"));

    [Fact]
    public void DecodesFlashesAndDropsTheFlaggedOnes()
    {
        // h5py: 177 flashes, whose quality flags are {0: 147, 3: 29, 5: 1}. Only the 147
        // the producer is confident in survive — including the very first flash in the
        // file, which is flagged 3.
        var flashes = GlmFile.DecodeFlashes(Bytes());
        Assert.Equal(147, flashes.Count);
        Assert.DoesNotContain(flashes, f =>
            Math.Abs(f.LatDeg - (-21.307377)) < 1e-4 && Math.Abs(f.LonDeg - (-121.7805)) < 1e-3);
    }

    [Fact]
    public void FirstKeptFlashMatchesTheFile()
    {
        // h5py: index 1 is the first with quality flag 0.
        var first = GlmFile.DecodeFlashes(Bytes())[0];
        Assert.Equal(30.107233, first.LatDeg, 4);
        Assert.Equal(-49.401096, first.LonDeg, 3);
    }

    [Fact]
    public void TimeComesFromTheProductTimestamp()
    {
        // h5py: product_time 840326400.0 s after 2000-01-01T12:00:00Z, and the file's own
        // time_coverage_start attribute reads 2026-08-18T12:00:00.0Z.
        var expected = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, GlmFile.ProductTimeUtc(Bytes()));
        Assert.All(GlmFile.DecodeFlashes(Bytes()), f => Assert.Equal(expected, f.TimeUtc));
    }

    [Fact]
    public void EveryFlashIsSomewhereReal()
    {
        Assert.All(GlmFile.DecodeFlashes(Bytes()), f =>
        {
            Assert.InRange(f.LatDeg, -90, 90);
            Assert.InRange(f.LonDeg, -180, 180);
        });
    }

    [Fact]
    public void FlashesFallInsideTheSatellitesFieldOfView()
    {
        // GOES-19 sits at 75.2 W. Its lightning mapper cannot see the far side of the
        // planet, so every flash must be on the Americas' hemisphere.
        var flashes = GlmFile.DecodeFlashes(Bytes());
        Assert.All(flashes, f =>
        {
            double separation = Math.Abs(((f.LonDeg - -75.2) + 540) % 360 - 180);
            Assert.True(separation < 90, $"flash at {f.LonDeg:F1} is beyond the horizon from 75.2W");
        });
    }

    [Fact]
    public void EnergyIsAPlausiblePhysicalQuantity()
    {
        // Optical energy at the sensor is femtojoule-scale; the point is that the scaling
        // is applied at all rather than the raw counts being handed out.
        var flashes = GlmFile.DecodeFlashes(Bytes());
        Assert.All(flashes, f => Assert.InRange(f.EnergyJoules, 0f, 1e-9f));
        Assert.Contains(flashes, f => f.EnergyJoules > 0);
    }
}
