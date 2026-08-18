namespace OpenWSR.Nexrad.Tests;

/// <summary>
/// Phase 6 acceptance: replaying the committed chunk corpus — in randomized order —
/// must produce decoded sweeps identical to the archive version of the same scan
/// (KTLX 2026-08-16 08:20:09Z, 55 chunks S..E).
/// </summary>
public class ChunkReplayTests
{
    private static readonly string ChunkDir = TestData.Path("chunks/KTLX");

    private static List<(int Seq, ChunkType Type, byte[] Bytes)> LoadVolume1Chunks()
    {
        var chunks = new List<(int, ChunkType, byte[])>();
        foreach (var file in Directory.GetFiles(ChunkDir, "KTLX_1_20260816-082009-*"))
        {
            var name = Path.GetFileName(file);
            var parts = name.Split('-');
            int seq = int.Parse(parts[^2]);
            var type = parts[^1] switch
            {
                "S" => ChunkType.Start,
                "E" => ChunkType.End,
                _ => ChunkType.Intermediate,
            };
            chunks.Add((seq, type, File.ReadAllBytes(file)));
        }
        Assert.Equal(55, chunks.Count);
        return chunks;
    }

    private static RadarVolume DecodeArchiveGroundTruth() =>
        ArchiveFile.DecodeFile(TestData.Path("KTLX20260816_082009_V06"));

    [Fact]
    public void InOrderReplayMatchesArchive()
    {
        var assembler = new LiveVolumeAssembler();
        foreach (var (seq, type, bytes) in LoadVolume1Chunks().OrderBy(c => c.Seq))
            Assert.True(assembler.AddChunk(seq, type, bytes));

        Assert.True(assembler.SawStart);
        Assert.True(assembler.SawEnd);
        AssertVolumesEqual(DecodeArchiveGroundTruth(), assembler.BuildSnapshot());
    }

    [Fact]
    public void RandomizedReplayMatchesArchive()
    {
        var chunks = LoadVolume1Chunks();
        var rng = new Random(20130520); // fixed seed: reproducible shuffles
        var shuffled = chunks.OrderBy(_ => rng.Next()).ToList();

        var assembler = new LiveVolumeAssembler();
        foreach (var (seq, type, bytes) in shuffled)
            assembler.AddChunk(seq, type, bytes);

        AssertVolumesEqual(DecodeArchiveGroundTruth(), assembler.BuildSnapshot());
    }

    [Fact]
    public void DuplicateChunksAreIgnoredAndHarmless()
    {
        var chunks = LoadVolume1Chunks();
        var assembler = new LiveVolumeAssembler();
        foreach (var (seq, type, bytes) in chunks)
            Assert.True(assembler.AddChunk(seq, type, bytes));
        foreach (var (seq, type, bytes) in chunks.Take(10))
            Assert.False(assembler.AddChunk(seq, type, bytes)); // duplicates rejected

        AssertVolumesEqual(DecodeArchiveGroundTruth(), assembler.BuildSnapshot());
    }

    [Fact]
    public void MidVolumeJoinWithoutStartChunkStillRenders()
    {
        // Arriving mid-volume: the S chunk (and 9 more) never arrive.
        var late = LoadVolume1Chunks().Where(c => c.Seq > 10).ToList();
        var assembler = new LiveVolumeAssembler();
        foreach (var (seq, type, bytes) in late)
            assembler.AddChunk(seq, type, bytes);

        Assert.False(assembler.SawStart);
        Assert.True(assembler.HasData);
        var partial = assembler.BuildSnapshot();
        Assert.Equal("KTLX", partial.SiteId);
        Assert.NotEmpty(partial.Sweeps);
        Assert.Equal(35.3334, partial.LatDeg, 3); // RVOL blocks ride along in every radial
    }

    [Fact]
    public void PartialVolumeExposesCompletedCutsIncrementally()
    {
        var chunks = LoadVolume1Chunks().OrderBy(c => c.Seq).ToList();
        var assembler = new LiveVolumeAssembler();

        foreach (var (seq, type, bytes) in chunks.Take(8))
            assembler.AddChunk(seq, type, bytes);
        int early = assembler.CompletedCuts.Count;
        Assert.True(early >= 1, "the lowest tilt should complete within the first few chunks");

        foreach (var (seq, type, bytes) in chunks.Skip(8))
            assembler.AddChunk(seq, type, bytes);
        Assert.True(assembler.CompletedCuts.Count > early);
    }

    private static void AssertVolumesEqual(RadarVolume expected, RadarVolume actual)
    {
        Assert.Equal(expected.SiteId, actual.SiteId);
        Assert.Equal(expected.LatDeg, actual.LatDeg);
        Assert.Equal(expected.LonDeg, actual.LonDeg);
        Assert.Equal(expected.VcpNumber, actual.VcpNumber);
        Assert.Equal(expected.Sweeps.Count, actual.Sweeps.Count);

        foreach (var (e, a) in expected.Sweeps.Zip(actual.Sweeps))
        {
            Assert.Equal(e.ElevationIndex, a.ElevationIndex);
            Assert.Equal(e.Moment, a.Moment);
            Assert.Equal(e.ScanTimeUtc, a.ScanTimeUtc);
            Assert.Equal(e.RadialCount, a.RadialCount);
            Assert.Equal(e.GateCount, a.GateCount);
            Assert.Equal(e.FirstGateM, a.FirstGateM);
            Assert.Equal(e.GateSpacingM, a.GateSpacingM);
            Assert.Equal(e.AzimuthsDeg, a.AzimuthsDeg);
            Assert.Equal(e.Data, a.Data); // exact float equality, NaN-aware
        }
    }
}
