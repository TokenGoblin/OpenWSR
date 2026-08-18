namespace OpenWSR.Nexrad;

public enum ChunkType { Start, Intermediate, End }

/// <summary>
/// Assembles one volume from real-time chunks. Only the S chunk carries the 24-byte
/// volume header; I/E chunks are bare LDM record streams. Because every LDM record is
/// self-contained (each radial carries its own RVOL/RELV/RAD blocks), chunks can be
/// ingested in any order, with gaps, and starting mid-volume (no S seen) — the builder
/// accumulates radials either way. Partial volumes are renderable at any point via
/// <see cref="BuildSnapshot"/>.
/// </summary>
public sealed class LiveVolumeAssembler
{
    private readonly VolumeBuilder _builder = new();
    private readonly HashSet<int> _seenSequences = [];
    private readonly Lock _lock = new();

    public VolumeHeader? Header { get; private set; }
    public bool SawStart { get; private set; }
    public bool SawEnd { get; private set; }
    public int ChunkCount { get; private set; }

    /// <summary>Ingest one chunk; returns false for duplicates (already-seen sequence numbers).</summary>
    public bool AddChunk(int sequence, ChunkType type, ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
        {
            if (!_seenSequences.Add(sequence))
                return false;

            var payload = bytes;
            if (type == ChunkType.Start)
            {
                SawStart = true;
                if (payload.Length >= VolumeHeader.Size)
                {
                    Header = VolumeHeader.Parse(payload);
                    payload = payload[VolumeHeader.Size..];
                }
            }
            if (type == ChunkType.End)
                SawEnd = true;

            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            foreach (var record in LdmRecords.Enumerate(stream))
                _builder.AddRecord(record);

            ChunkCount++;
            return true;
        }
    }

    /// <summary>Elevation cuts whose end-of-elevation radial has arrived.</summary>
    public IReadOnlyList<int> CompletedCuts
    {
        get { lock (_lock) return [.. _builder.CompletedCuts]; }
    }

    public bool HasData
    {
        get { lock (_lock) return _builder.HasData; }
    }

    /// <summary>Build the volume from everything received so far (partial is fine).</summary>
    public RadarVolume BuildSnapshot()
    {
        lock (_lock)
            return _builder.Build();
    }
}
