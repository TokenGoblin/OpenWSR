using OpenWSR.Geo;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OpenWSR.Render.Tiles;

/// <summary>
/// VRAM LRU cache of tile textures, owned by the render thread. Capped at ~512 MB:
/// a 256x256 BGRA tile is 256 KB, so 2000 tiles ≈ 500 MB.
/// </summary>
public sealed class TileTextureCache(ID3D11Device device, int capacity = 2000) : IDisposable
{
    private sealed class Entry
    {
        public required ID3D11Texture2D Texture;
        public required ID3D11ShaderResourceView View;
        public long LastUsedFrame;
    }

    private readonly Dictionary<TileKey, Entry> _entries = [];
    private long _frame;

    public void BeginFrame() => _frame++;

    public bool TryGet(TileKey key, out ID3D11ShaderResourceView view)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.LastUsedFrame = _frame;
            view = entry.View;
            return true;
        }
        view = null!;
        return false;
    }

    public bool Contains(TileKey key) => _entries.ContainsKey(key);

    public unsafe void Add(TileKey key, byte[] bgra)
    {
        if (_entries.ContainsKey(key)) return;
        Evict();

        var desc = new Texture2DDescription
        {
            Width = TileMath.TilePixels,
            Height = TileMath.TilePixels,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
        };
        fixed (byte* p = bgra)
        {
            var data = new SubresourceData((IntPtr)p, TileMath.TilePixels * 4);
            var texture = device.CreateTexture2D(desc, [data]);
            var view = device.CreateShaderResourceView(texture);
            _entries[key] = new Entry { Texture = texture, View = view, LastUsedFrame = _frame };
        }
    }

    private void Evict()
    {
        if (_entries.Count < capacity) return;
        // Drop the ~10% least recently used so eviction is amortized, never per-tile.
        foreach (var (key, entry) in _entries
                     .OrderBy(kv => kv.Value.LastUsedFrame)
                     .Take(capacity / 10)
                     .ToList())
        {
            entry.View.Dispose();
            entry.Texture.Dispose();
            _entries.Remove(key);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.View.Dispose();
            entry.Texture.Dispose();
        }
        _entries.Clear();
    }
}
