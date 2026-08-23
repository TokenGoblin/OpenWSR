using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenWSR.NetCdf;

public sealed class Hdf5FormatException(string message) : Exception(message);

/// <summary>How a dataset's numbers are stored, once the HDF5 datatype message is decoded.</summary>
public enum Hdf5Kind
{
    SignedInteger,
    UnsignedInteger,
    Float,

    /// <summary>Fixed-length characters. Attributes only — no dataset here stores text.</summary>
    String,
}

/// <summary>
/// One attribute hanging off a dataset or off the root group: the small named values that
/// say how to interpret the big array beside them.
///
/// These matter more than their size suggests. A NetCDF-4 variable of scaled integers is
/// meaningless without its <c>scale_factor</c> and <c>add_offset</c>, and a projected grid
/// is unplaceable without the projection constants — all of which are attributes.
/// </summary>
public sealed record Hdf5Attribute(
    string Name,
    Hdf5Kind Kind,
    int ElementSize,
    bool LittleEndian,
    long[] Dimensions,
    byte[] Raw)
{
    public long Count => Dimensions.Length == 0 ? 1 : Dimensions.Aggregate(1L, (a, b) => a * b);

    /// <summary>The value as a number. Rank-1 attributes of one element read as scalars.</summary>
    public double AsDouble(int index = 0)
    {
        if (Kind == Hdf5Kind.String)
            throw new Hdf5FormatException($"Attribute '{Name}' is text, not a number.");
        if (index < 0 || index >= Count)
            throw new Hdf5FormatException($"Attribute '{Name}' has no element {index}.");
        return MiniHdf5.ToDoubleAt(Raw, index, ElementSize, LittleEndian, Kind);
    }

    /// <summary>
    /// The value as text. HDF5 pads fixed-length strings with NULs or spaces, and neither
    /// belongs in a comparison against something like "x".
    /// </summary>
    public string AsString() =>
        System.Text.Encoding.UTF8.GetString(Raw).TrimEnd('\0', ' ');
}

/// <summary>One array in the file, located but not yet read.</summary>
public sealed record Hdf5Dataset(
    string Name,
    long[] Dimensions,
    Hdf5Kind Kind,
    int ElementSize,
    bool LittleEndian)
{
    /// <summary>Total elements. A scalar dataset has rank 0 and exactly one element.</summary>
    public long Count => Dimensions.Length == 0 ? 1 : Dimensions.Aggregate(1L, (a, b) => a * b);

    internal long ContiguousAddress { get; init; } = -1;
    internal long ChunkTreeAddress { get; init; } = -1;
    internal int[] ChunkDimensions { get; init; } = [];
    internal bool Shuffle { get; init; }
    internal bool Deflate { get; init; }

    /// <summary>Where this object's header lives, so its attributes can be read on demand.</summary>
    internal long HeaderAddress { get; init; } = -1;
}

/// <summary>
/// Just enough HDF5 to read a NetCDF-4 file.
///
/// GOES lightning is distributed only as NetCDF-4, which is HDF5 underneath, and pulling
/// in a full HDF5 stack for four arrays of floats would be the tail wagging the dog —
/// <c>MiniPng</c> exists inside <c>OpenWSR.Grib2</c> for exactly this reason. What is
/// implemented is the subset those files actually use: superblock v2, version-2 object
/// headers with continuation blocks, dense links in a fractal heap, version-2 dataspaces,
/// version-1 datatypes, version-3 contiguous and chunked layouts indexed by a version-1
/// B-tree, and the shuffle and deflate filters.
///
/// Deliberately absent: version-0 and version-1 superblocks, old-style symbol-table groups,
/// compound and variable-length datatypes, huge or tiny fractal-heap objects, and every
/// filter but those two. A file using them raises rather than guesses.
/// </summary>
public sealed class MiniHdf5
{
    private const ulong Undefined = 0xFFFFFFFFFFFFFFFF;

    private readonly byte[] _data;
    private readonly Dictionary<string, Hdf5Dataset> _datasets = new(StringComparer.Ordinal);
    private long _rootHeader = -1;

    public IReadOnlyDictionary<string, Hdf5Dataset> Datasets => _datasets;

    private MiniHdf5(byte[] data) => _data = data;

    public static MiniHdf5 Open(byte[] data)
    {
        var file = new MiniHdf5(data);
        file.ReadSuperblock();
        return file;
    }

    // ---- superblock ----

    private void ReadSuperblock()
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'H', (byte)'D', (byte)'F', 0x0D, 0x0A, 0x1A, 0x0A];
        if (_data.Length < 48 || !_data.AsSpan(0, 8).SequenceEqual(signature))
            throw new Hdf5FormatException("Not an HDF5 file — signature missing.");

        int version = _data[8];
        if (version != 2 && version != 3)
            throw new Hdf5FormatException(
                $"HDF5 superblock version {version} is not supported (only 2 and 3 are).");
        if (_data[9] != 8 || _data[10] != 8)
            throw new Hdf5FormatException("Only 8-byte offsets and lengths are supported.");

        // Superblock v2: signature 0-7, version 8, offset/length sizes 9-10, flags 11,
        // then base address 12, extension 20, end-of-file 28, root group header 36.
        long rootHeader = _rootHeader = (long)U64(36);
        foreach (var (name, address) in ReadGroupLinks(rootHeader))
        {
            var dataset = TryReadDataset(name, address);
            if (dataset is not null) _datasets[name] = dataset;
        }
    }

    // ---- object headers ----

    private readonly record struct Message(byte Type, int Offset, int Size);

    /// <summary>
    /// Every message of a version-2 object header, following continuation blocks. A dataset
    /// of any complexity spills into one — attributes alone push it over — so not following
    /// them silently loses the layout message and the dataset reads as empty.
    /// </summary>
    private List<Message> ReadMessages(long headerAddress)
    {
        var messages = new List<Message>();
        var pending = new Queue<(long Address, long Length, bool IsContinuation)>();
        pending.Enqueue((headerAddress, 0, false));

        while (pending.Count > 0)
        {
            var (address, length, isContinuation) = pending.Dequeue();
            int p = checked((int)address);
            byte flags;
            int end;

            if (isContinuation)
            {
                if (!Match(p, "OCHK"))
                    throw new Hdf5FormatException("Object header continuation block is malformed.");
                p += 4;
                flags = _continuationFlags;
                end = checked((int)(address + length)) - 4; // trailing checksum
            }
            else
            {
                if (!Match(p, "OHDR"))
                    throw new Hdf5FormatException("Only version-2 object headers are supported.");
                p += 4;
                if (_data[p++] != 2)
                    throw new Hdf5FormatException("Only version-2 object headers are supported.");
                flags = _data[p++];
                _continuationFlags = flags;
                if ((flags & 0x20) != 0) p += 16;   // access/modification/change/birth times
                if ((flags & 0x10) != 0) p += 4;    // max compact / min dense
                int sizeBytes = 1 << (flags & 0x03);
                long chunkSize = ReadVariable(p, sizeBytes);
                p += sizeBytes;
                end = p + checked((int)chunkSize);
            }

            while (p < end - 3)
            {
                byte type = _data[p++];
                int size = U16(p); p += 2;
                p++;                                 // message flags
                if ((flags & 0x04) != 0) p += 2;     // creation order
                if (p + size > _data.Length) break;

                if (type == 0x10) // object header continuation
                    pending.Enqueue(((long)U64(p), (long)U64(p + 8), true));
                else
                    messages.Add(new Message(type, p, size));
                p += size;
            }
        }
        return messages;
    }

    private byte _continuationFlags;

    // ---- links: enumerate the fractal heap rather than searching the B-tree ----

    /// <summary>
    /// Name-to-address for everything in a group.
    ///
    /// Dense links live in a fractal heap with a version-2 B-tree beside it for lookup by
    /// name. Since every link is wanted anyway, the heap is walked directly and the B-tree
    /// is never touched — which removes the single most intricate structure in the format
    /// at the cost of reading a few kilobytes that a search would have skipped.
    /// </summary>
    private List<(string Name, long Address)> ReadGroupLinks(long headerAddress)
    {
        var links = new List<(string, long)>();

        foreach (var message in ReadMessages(headerAddress))
        {
            if (message.Type == 0x06) // a compact link, stored in the header itself
            {
                if (TryReadLink(message.Offset, message.Offset + message.Size, out var link, out _))
                    links.Add(link);
                continue;
            }
            if (message.Type != 0x02) continue; // link info

            int p = message.Offset;
            p++;                                  // version
            byte flags = _data[p++];
            if ((flags & 0x01) != 0) p += 8;      // maximum creation index
            ulong heap = U64(p);
            if (heap != Undefined) links.AddRange(ReadFractalHeapLinks((long)heap));
        }
        return links;
    }

    /// <summary>The payload ranges of every direct block in a fractal heap.</summary>
    private List<(int Start, int End)> ReadFractalHeapBlocks(long heapAddress)
    {
        int p = checked((int)heapAddress);
        if (!Match(p, "FRHP"))
            throw new Hdf5FormatException("Fractal heap header is malformed.");
        p += 4;
        p++;                                       // version
        int idLength = U16(p); p += 2;
        int filterLength = U16(p); p += 2;
        p++;                                       // flags
        if (filterLength != 0)
            throw new Hdf5FormatException("Filtered fractal heaps are not supported.");

        p += 4;                                    // maximum managed object size
        p += 8 * 4;                                // huge object bookkeeping
        p += 8 * 4;                                // free space and managed space
        p += 8 * 4;                                // object counts
        int tableWidth = U16(p); p += 2;
        long startingBlockSize = (long)U64(p); p += 8;
        p += 8;                                    // maximum direct block size
        int maxHeapBits = U16(p); p += 2;
        p += 2;                                    // starting rows in the root indirect block
        ulong rootAddress = U64(p); p += 8;
        int currentRows = U16(p);

        int offsetBytes = (maxHeapBits + 7) / 8;
        var blocks = new List<(int Start, int End)>();
        if (rootAddress == Undefined) return blocks;

        if (currentRows == 0)
        {
            AddDirectBlock((long)rootAddress, startingBlockSize, offsetBytes, blocks);
            return blocks;
        }

        int q = checked((int)rootAddress);
        if (!Match(q, "FHIB"))
            throw new Hdf5FormatException("Fractal heap indirect block is malformed.");
        q += 4 + 1 + 8 + offsetBytes;

        for (int row = 0; row < currentRows; row++)
        {
            // Rows 0 and 1 share the starting size; every row after that doubles.
            long blockSize = row < 2 ? startingBlockSize : startingBlockSize << (row - 1);
            for (int column = 0; column < tableWidth; column++)
            {
                ulong child = U64(q); q += 8;
                if (child != Undefined)
                    AddDirectBlock((long)child, blockSize, offsetBytes, blocks);
            }
        }
        return blocks;
    }

    /// <summary>The payload range of one direct block, past its header.</summary>
    private void AddDirectBlock(
        long address, long size, int offsetBytes, List<(int Start, int End)> blocks)
    {
        int p = checked((int)address);
        if (!Match(p, "FHDB")) return;
        p += 4 + 1 + 8 + offsetBytes + 4;          // signature, version, heap address, offset, checksum
        int end = (int)Math.Min(address + size, _data.Length);
        if (p < end) blocks.Add((p, end));
    }

    /// <summary>
    /// Every link record in a heap.
    ///
    /// The heap does not delimit its objects — the B-tree's heap IDs carry the offsets, and
    /// that is exactly what is being skipped. Instead each candidate position is validated
    /// as a link record (version, reserved flag bits, a plausible printable name, an address
    /// inside the file); anything that fails is not a record, so the scan advances a byte
    /// and tries again. Free space between records is skipped this way rather than parsed.
    /// </summary>
    private List<(string Name, long Address)> ReadFractalHeapLinks(long heapAddress)
    {
        var links = new List<(string, long)>();
        foreach (var (start, end) in ReadFractalHeapBlocks(heapAddress))
        {
            int p = start;
            while (p < end - 12)
            {
                if (TryReadLink(p, end, out var link, out int next))
                {
                    links.Add(link);
                    p = next;
                }
                else
                {
                    p++;
                }
            }
        }
        return links;
    }

    /// <summary>
    /// Every attribute record in a heap, found the same way and for the same reason as the
    /// links above. Validation has to be strict here because the scan will land mid-record
    /// constantly: an attribute that does not name itself in printable characters, describe
    /// a datatype this reader knows, and fit its own data inside the block is not one.
    /// </summary>
    private List<Hdf5Attribute> ReadFractalHeapAttributes(long heapAddress)
    {
        var attributes = new List<Hdf5Attribute>();
        foreach (var (start, end) in ReadFractalHeapBlocks(heapAddress))
        {
            int p = start;
            while (p < end - 12)
            {
                if (TryReadAttribute(p, end, out var attribute, out int next) && attribute is not null)
                {
                    attributes.Add(attribute);
                    p = next;
                }
                else
                {
                    p++;
                }
            }
        }
        return attributes;
    }

    private bool TryReadLink(int start, int end, out (string Name, long Address) link, out int next)
    {
        link = default;
        next = start;

        if (start + 2 > end) return false;
        byte version = _data[start];
        byte flags = _data[start + 1];
        if (version != 0 || (flags & 0xE0) != 0) return false;

        int p = start + 2;
        if ((flags & 0x08) != 0) p += 8;           // creation order
        if ((flags & 0x04) != 0)
        {
            if (p >= end || _data[p] != 0) return false; // only hard links
            p++;
        }
        if ((flags & 0x10) != 0) p++;              // name character set

        int lengthBytes = 1 << (flags & 0x03);
        if (p + lengthBytes > end) return false;
        long nameLength = ReadVariable(p, lengthBytes);
        p += lengthBytes;

        if (nameLength is < 1 or > 255 || p + nameLength + 8 > end) return false;
        for (int i = 0; i < nameLength; i++)
        {
            byte c = _data[p + i];
            if (c is < 0x20 or >= 0x7F) return false;
        }

        string name = System.Text.Encoding.ASCII.GetString(_data, p, (int)nameLength);
        p += (int)nameLength;

        ulong target = U64(p);
        if (target >= (ulong)_data.Length) return false;
        p += 8;

        link = (name, (long)target);
        next = p;
        return true;
    }

    // ---- datasets ----

    /// <summary>
    /// A datatype message, shared by datasets and by the copy embedded in every attribute.
    /// Class 3 is fixed-length text, which only ever turns up on attributes — no array in
    /// these files stores strings — but <c>sweep_angle_axis</c> is one and it decides the
    /// geometry of a whole projection, so it cannot be skipped.
    /// </summary>
    private (Hdf5Kind Kind, int ElementSize, bool LittleEndian) ParseDatatype(int p, string owner)
    {
        int typeClass = _data[p] & 0x0F;
        byte bits = _data[p + 1];
        int elementSize = (int)U32(p + 4);
        var kind = typeClass switch
        {
            0 => (bits & 0x08) != 0 ? Hdf5Kind.SignedInteger : Hdf5Kind.UnsignedInteger,
            1 => Hdf5Kind.Float,
            3 => Hdf5Kind.String,
            _ => throw new Hdf5FormatException(
                $"Datatype class {typeClass} in '{owner}' is not supported."),
        };
        // Byte order lives in bit 0 for numbers; a string's bit 0 means something else
        // entirely (padding), and text has no endianness to get wrong.
        bool littleEndian = kind == Hdf5Kind.String || (bits & 0x01) == 0;
        return (kind, elementSize, littleEndian);
    }

    private long[] ParseDataspace(int p)
    {
        byte version = _data[p];
        int rank = _data[p + 1];
        int q = p + (version == 1 ? 8 : 4);
        var dimensions = new long[rank];
        for (int i = 0; i < rank; i++) { dimensions[i] = (long)U64(q); q += 8; }
        return dimensions;
    }

    /// <summary>
    /// The attributes attached to a dataset, or to the root group when <paramref name="name"/>
    /// is null. Parsed on demand rather than at open: a NetCDF-4 file carries hundreds of them
    /// and a caller wants two or three.
    /// </summary>
    public IReadOnlyDictionary<string, Hdf5Attribute> AttributesOf(string? name = null)
    {
        long header;
        if (name is null)
        {
            header = _rootHeader;
        }
        else if (_datasets.TryGetValue(name, out var dataset) && dataset.HeaderAddress >= 0)
        {
            header = dataset.HeaderAddress;
        }
        else
        {
            throw new Hdf5FormatException($"'{name}' is not in this file.");
        }

        var result = new Dictionary<string, Hdf5Attribute>(StringComparer.Ordinal);
        foreach (var message in ReadMessages(header))
        {
            switch (message.Type)
            {
                // Compact storage: the attribute sits in the object header itself.
                case 0x0C:
                    if (TryReadAttribute(message.Offset, message.Offset + message.Size,
                            out var inline, out _) && inline is not null)
                        result[inline.Name] = inline;
                    break;

                // Attribute info: past a handful of attributes HDF5 moves them out to a
                // fractal heap and leaves only this pointer behind. Every netCDF-4 variable
                // crosses that threshold, so in practice this is the only path that fires.
                case 0x15:
                {
                    int p = message.Offset;
                    p++;                                  // version
                    byte infoFlags = _data[p++];
                    if ((infoFlags & 0x01) != 0) p += 2;  // maximum creation index
                    ulong heap = U64(p);
                    if (heap == Undefined) break;
                    foreach (var dense in ReadFractalHeapAttributes((long)heap))
                        result[dense.Name] = dense;
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// One attribute record. The three versions differ only in whether the name and the two
    /// embedded messages are padded out to eight-byte boundaries — version 1 pads all three,
    /// 2 and 3 pad none — and version 3 inserts a character-encoding byte before the name.
    /// Getting the padding wrong lands the data pointer inside the dataspace and the value
    /// comes back as plausible nonsense, so this is worth being exact about.
    ///
    /// Returns false rather than throwing on anything malformed: the dense path calls this at
    /// every byte offset of a heap block, so "not an attribute here" is the common answer.
    /// </summary>
    private bool TryReadAttribute(int offset, int end, out Hdf5Attribute? attribute, out int next)
    {
        attribute = null;
        next = offset;
        if (offset < 0 || offset + 8 > end) return false;

        byte version = _data[offset];
        if (version is < 1 or > 3) return false;

        int p = offset + 2;                      // version, then flags (v1: reserved)
        int nameSize = U16(p); p += 2;
        int datatypeSize = U16(p); p += 2;
        int dataspaceSize = U16(p); p += 2;
        if (version == 3) p++;                   // name character-set encoding

        // A netCDF attribute name is a short identifier and its datatype and dataspace
        // messages are a few dozen bytes. These bounds are what stop the scan latching on
        // to arbitrary data that happens to start with a 1, 2 or 3.
        // A datatype message is at least its 8-byte prefix. A dataspace message can be as
        // short as 4: that is a *scalar*, version and rank and two flag bytes with no
        // dimensions after it — which is exactly how every text attribute is stored, so a
        // floor of 8 here silently drops all of them and keeps only the arrays.
        if (nameSize is < 2 or > 256) return false;
        if (datatypeSize is < 8 or > 256) return false;
        if (dataspaceSize is < 4 or > 256) return false;

        bool padded = version == 1;
        if (p + nameSize > end) return false;
        for (int i = 0; i < nameSize - 1; i++)
            if (_data[p + i] < 0x20 || _data[p + i] > 0x7E) return false;
        if (_data[p + nameSize - 1] != 0) return false;

        string name = System.Text.Encoding.UTF8.GetString(_data, p, nameSize - 1);
        p += padded ? Align8(nameSize) : nameSize;

        if (p + datatypeSize > end) return false;
        int typeClass = _data[p] & 0x0F;
        if (typeClass is not (0 or 1 or 3)) return false;   // integer, float, fixed-length text
        int elementSize = (int)U32(p + 4);
        if (elementSize < 1 || elementSize > 4096) return false;
        var (kind, _, littleEndian) = ParseDatatype(p, name);
        p += padded ? Align8(datatypeSize) : datatypeSize;

        if (p + dataspaceSize > end) return false;
        int rank = _data[p + 1];
        if (rank > 2) return false;
        var dimensions = ParseDataspace(p);
        p += padded ? Align8(dataspaceSize) : dataspaceSize;

        long count = dimensions.Length == 0 ? 1 : dimensions.Aggregate(1L, (a, b) => a * b);
        if (count is < 1 or > 65536) return false;
        long bytes = count * elementSize;
        if (p + bytes > end) return false;

        attribute = new Hdf5Attribute(
            name, kind, elementSize, littleEndian, dimensions,
            _data.AsSpan(p, (int)bytes).ToArray());
        next = (int)(p + bytes);
        return true;
    }

    private static int Align8(int n) => (n + 7) & ~7;


    private Hdf5Dataset? TryReadDataset(string name, long headerAddress)
    {
        long[] dimensions = [];
        Hdf5Kind kind = Hdf5Kind.Float;
        int elementSize = 0;
        bool littleEndian = true;
        long contiguous = -1;
        long chunkTree = -1;
        int[] chunkDimensions = [];
        bool shuffle = false, deflate = false;
        bool sawLayout = false, sawType = false;

        foreach (var message in ReadMessages(headerAddress))
        {
            int p = message.Offset;
            switch (message.Type)
            {
                case 0x01: // dataspace
                    dimensions = ParseDataspace(p);
                    break;
                case 0x03: // datatype
                    (kind, elementSize, littleEndian) = ParseDatatype(p, name);
                    sawType = true;
                    break;
                case 0x08: // data layout
                {
                    byte version = _data[p];
                    byte layoutClass = _data[p + 1];
                    if (version != 3 && version != 4)
                        throw new Hdf5FormatException($"Data layout version {version} is not supported.");
                    if (layoutClass == 1)
                    {
                        contiguous = (long)U64(p + 2);
                        sawLayout = true;
                    }
                    else if (layoutClass == 2 && version == 3)
                    {
                        int rank = _data[p + 2];        // data rank plus one for element size
                        chunkTree = (long)U64(p + 3);
                        chunkDimensions = new int[rank];
                        for (int i = 0; i < rank; i++) chunkDimensions[i] = (int)U32(p + 11 + 4 * i);
                        sawLayout = true;
                    }
                    else
                    {
                        // Compact, virtual, and the 1.10 chunk indexes. Skipping the dataset
                        // is right: a lightning file has none, and inventing values would be
                        // worse than reporting it missing.
                        return null;
                    }
                    break;
                }
                case 0x0B: // filter pipeline
                {
                    byte version = _data[p];
                    int count = _data[p + 1];
                    int q = p + (version == 1 ? 8 : 2);
                    for (int i = 0; i < count; i++)
                    {
                        int id = U16(q);
                        // Version 1 always carries a name length; version 2 carries one only
                        // for filters outside the reserved range, so its header is two bytes
                        // shorter and reading it as v1 lands on the wrong filter id entirely.
                        bool hasName = version == 1 || id >= 256;
                        int nameLength = hasName ? U16(q + 2) : 0;
                        int header = hasName ? 8 : 6;
                        int valueCount = U16(q + header - 2);
                        q += header + nameLength + 4 * valueCount;
                        if (version == 1 && nameLength % 8 != 0) q += 8 - nameLength % 8;
                        if (version == 1 && valueCount % 2 != 0) q += 4;
                        switch (id)
                        {
                            case 1: deflate = true; break;
                            case 2: shuffle = true; break;
                            default:
                                throw new Hdf5FormatException(
                                    $"Filter {id} on '{name}' is not supported (only shuffle and deflate).");
                        }
                    }
                    break;
                }
            }
        }

        if (!sawLayout || !sawType || elementSize == 0) return null;

        return new Hdf5Dataset(name, dimensions, kind, elementSize, littleEndian)
        {
            HeaderAddress = headerAddress,
            ContiguousAddress = contiguous,
            ChunkTreeAddress = chunkTree,
            ChunkDimensions = chunkDimensions,
            Shuffle = shuffle,
            Deflate = deflate,
        };
    }

    // ---- reading ----

    /// <summary>Raw element bytes of a dataset, filters undone and chunks assembled.</summary>
    public byte[] ReadRaw(Hdf5Dataset dataset)
    {
        long total = dataset.Count * dataset.ElementSize;
        if (total > int.MaxValue) throw new Hdf5FormatException($"'{dataset.Name}' is too large to read.");
        var result = new byte[total];

        if (dataset.ContiguousAddress >= 0)
        {
            int available = (int)Math.Min(total, _data.Length - dataset.ContiguousAddress);
            Array.Copy(_data, dataset.ContiguousAddress, result, 0, Math.Max(0, available));
            return result;
        }

        // Chunked: each B-tree leaf entry gives a chunk's address, stored size and the
        // element offset it starts at.
        long elementsPerChunk = 1;
        for (int i = 0; i < dataset.ChunkDimensions.Length - 1; i++)
            elementsPerChunk *= dataset.ChunkDimensions[i];

        foreach (var chunk in ReadChunkTree(dataset.ChunkTreeAddress, dataset.ChunkDimensions.Length))
        {
            var bytes = _data.AsSpan(checked((int)chunk.Address), chunk.Size).ToArray();
            if (dataset.Deflate) bytes = Inflate(bytes);
            if (dataset.Shuffle) bytes = Unshuffle(bytes, dataset.ElementSize);

            long startElement = chunk.Offsets.Length > 1 ? chunk.Offsets[0] : 0;
            long destination = startElement * dataset.ElementSize;
            long length = Math.Min(bytes.Length, total - destination);
            if (destination >= 0 && length > 0)
                Array.Copy(bytes, 0, result, destination, length);
        }
        return result;
    }

    private readonly record struct Chunk(long Address, int Size, long[] Offsets);

    private List<Chunk> ReadChunkTree(long address, int rank)
    {
        var chunks = new List<Chunk>();
        if (address < 0 || (ulong)address == Undefined) return chunks;

        var pending = new Queue<long>();
        pending.Enqueue(address);
        while (pending.Count > 0)
        {
            int p = checked((int)pending.Dequeue());
            if (!Match(p, "TREE"))
                throw new Hdf5FormatException("Chunk index is not a version-1 B-tree.");
            p += 4;
            if (_data[p++] != 1)
                throw new Hdf5FormatException("Chunk index B-tree has the wrong node type.");
            int level = _data[p++];
            int used = U16(p); p += 2;
            p += 16;                                 // left and right siblings

            for (int i = 0; i < used; i++)
            {
                int size = (int)U32(p); p += 4;
                p += 4;                              // filter mask
                var offsets = new long[rank];
                for (int j = 0; j < rank; j++) { offsets[j] = (long)U64(p); p += 8; }
                long child = (long)U64(p); p += 8;

                if (level == 0) chunks.Add(new Chunk(child, size, offsets));
                else pending.Enqueue(child);
            }
        }
        return chunks;
    }

    /// <summary>
    /// HDF5's deflate filter writes zlib-wrapped streams, header and checksum included, so
    /// this is <see cref="ZLibStream"/> rather than a raw <see cref="DeflateStream"/>.
    /// </summary>
    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(compressed.Length * 4);
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Undo the shuffle filter, which stores every element's first byte, then every second
    /// byte, and so on — neighbouring values share high bytes, so deflate compresses the
    /// regrouped stream far better.
    /// </summary>
    private static byte[] Unshuffle(byte[] shuffled, int elementSize)
    {
        if (elementSize <= 1) return shuffled;
        int count = shuffled.Length / elementSize;
        var result = new byte[shuffled.Length];
        for (int b = 0; b < elementSize; b++)
        {
            int source = b * count;
            for (int i = 0; i < count; i++)
                result[i * elementSize + b] = shuffled[source + i];
        }
        return result;
    }

    // ---- typed accessors ----

    public float[] ReadSingle(string name) => Read(name, ToSingle);

    public double[] ReadDouble(string name) => Read(name, ToDouble);

    public short[] ReadInt16(string name) => Read(name, (span, little, size, kind) =>
        (short)ToInt64(span, little, size, kind));

    public int[] ReadInt32(string name) => Read(name, (span, little, size, kind) =>
        (int)ToInt64(span, little, size, kind));

    private T[] Read<T>(string name, Func<ReadOnlySpan<byte>, bool, int, Hdf5Kind, T> convert)
    {
        if (!_datasets.TryGetValue(name, out var dataset))
            throw new Hdf5FormatException($"'{name}' is not in this file.");

        var raw = ReadRaw(dataset);
        int size = dataset.ElementSize;
        var result = new T[dataset.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = convert(raw.AsSpan(i * size, size), dataset.LittleEndian, size, dataset.Kind);
        return result;
    }

    private static float ToSingle(ReadOnlySpan<byte> span, bool little, int size, Hdf5Kind kind) =>
        kind == Hdf5Kind.Float
            ? size == 8 ? (float)ReadF64(span, little) : ReadF32(span, little)
            : ToInt64(span, little, size, kind);

    private static double ToDouble(ReadOnlySpan<byte> span, bool little, int size, Hdf5Kind kind) =>
        kind == Hdf5Kind.Float
            ? size == 8 ? ReadF64(span, little) : ReadF32(span, little)
            : ToInt64(span, little, size, kind);

    /// <summary>One element of a packed buffer as a double. Shared with <see cref="Hdf5Attribute"/>.</summary>
    internal static double ToDoubleAt(byte[] raw, int index, int size, bool little, Hdf5Kind kind) =>
        ToDouble(raw.AsSpan(index * size, size), little, size, kind);

    private static long ToInt64(ReadOnlySpan<byte> span, bool little, int size, Hdf5Kind kind)
    {
        ulong value = 0;
        for (int i = 0; i < size; i++)
            value |= (ulong)span[little ? i : size - 1 - i] << (8 * i);

        if (kind != Hdf5Kind.SignedInteger || size >= 8) return (long)value;
        ulong sign = 1UL << (size * 8 - 1);
        return (value & sign) != 0 ? (long)(value | ~(sign * 2 - 1)) : (long)value;
    }

    private static float ReadF32(ReadOnlySpan<byte> s, bool little) => little
        ? BinaryPrimitives.ReadSingleLittleEndian(s)
        : BinaryPrimitives.ReadSingleBigEndian(s);

    private static double ReadF64(ReadOnlySpan<byte> s, bool little) => little
        ? BinaryPrimitives.ReadDoubleLittleEndian(s)
        : BinaryPrimitives.ReadDoubleBigEndian(s);

    // ---- primitives ----

    private bool Match(int offset, string signature) =>
        offset >= 0 && offset + 4 <= _data.Length &&
        _data[offset] == signature[0] && _data[offset + 1] == signature[1] &&
        _data[offset + 2] == signature[2] && _data[offset + 3] == signature[3];

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));
    private ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(offset));

    private long ReadVariable(int offset, int bytes)
    {
        long value = 0;
        for (int i = 0; i < bytes; i++) value |= (long)_data[offset + i] << (8 * i);
        return value;
    }
}
