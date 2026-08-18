using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OpenWSR.Render;

/// <summary>Overlay geometry kept in double-precision Mercator; transformed per frame.</summary>
public sealed class OverlayGeometry
{
    public List<(double X, double Y, uint Rgba)> FillTriangles { get; } = [];         // 3 entries per tri
    public List<(double Ax, double Ay, double Bx, double By, uint Rgba, float WidthPx)> Lines { get; } = [];

    public static uint Pack(byte r, byte g, byte b, byte a) =>
        (uint)(r | (g << 8) | (b << 16) | (a << 24));

    /// <summary>Ear-clip a simple polygon (Mercator coords) into FillTriangles.</summary>
    public void AddPolygonFill(IReadOnlyList<(double X, double Y)> ring, uint rgba)
    {
        var points = ring.ToList();
        if (points.Count >= 2 && points[0] == points[^1])
            points.RemoveAt(points.Count - 1);
        if (points.Count < 3) return;

        // Ensure counter-clockwise winding for the ear test.
        double signedArea = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var (x1, y1) = points[i];
            var (x2, y2) = points[(i + 1) % points.Count];
            signedArea += x1 * y2 - x2 * y1;
        }
        if (signedArea < 0) points.Reverse();

        var remaining = Enumerable.Range(0, points.Count).ToList();
        int guard = 0;
        while (remaining.Count > 3 && guard++ < 10_000)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int prev = remaining[(i - 1 + remaining.Count) % remaining.Count];
                int curr = remaining[i];
                int next = remaining[(i + 1) % remaining.Count];
                var a = points[prev];
                var b = points[curr];
                var c = points[next];

                double cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (cross <= 0) continue; // reflex vertex

                bool containsOther = false;
                foreach (var other in remaining)
                {
                    if (other == prev || other == curr || other == next) continue;
                    if (PointInTriangle(points[other], a, b, c))
                    {
                        containsOther = true;
                        break;
                    }
                }
                if (containsOther) continue;

                FillTriangles.Add((a.X, a.Y, rgba));
                FillTriangles.Add((b.X, b.Y, rgba));
                FillTriangles.Add((c.X, c.Y, rgba));
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break; // degenerate ring; draw what we have
        }
        if (remaining.Count == 3)
        {
            FillTriangles.Add((points[remaining[0]].X, points[remaining[0]].Y, rgba));
            FillTriangles.Add((points[remaining[1]].X, points[remaining[1]].Y, rgba));
            FillTriangles.Add((points[remaining[2]].X, points[remaining[2]].Y, rgba));
        }
    }

    public void AddPolygonOutline(IReadOnlyList<(double X, double Y)> ring, uint rgba, float widthPx)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            Lines.Add((a.X, a.Y, b.X, b.Y, rgba, widthPx));
        }
    }

    private static bool PointInTriangle(
        (double X, double Y) p, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        double d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);

        static double Sign((double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3) =>
            (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
    }
}

/// <summary>Draws overlay geometry (warning polygons, measure lines) above the radar layer.</summary>
public sealed class OverlayRenderer : IDisposable
{
    private const string ShaderSource = """
        struct VSIn { float2 pos : POSITION; float4 color : COLOR0; };
        struct VSOut { float4 pos : SV_Position; float4 color : COLOR0; };
        VSOut VSMain(VSIn i)
        {
            VSOut o;
            o.pos = float4(i.pos, 0.0, 1.0);
            o.color = i.color;
            return o;
        }
        float4 PSMain(VSOut i) : SV_Target
        {
            return float4(i.color.rgb * i.color.a, i.color.a); // premultiplied
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public float X, Y;
        public uint Rgba;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11RasterizerState _rasterizer;
    private ID3D11Buffer? _vertexBuffer;
    private int _vertexCapacity;
    private Vertex[] _staging = new Vertex[1024];

    public OverlayRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        Compiler.Compile(ShaderSource, "VSMain", "OverlayRenderer", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob is null) throw new InvalidOperationException($"Overlay VS: {vsErr?.AsString()}");
        Compiler.Compile(ShaderSource, "PSMain", "OverlayRenderer", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob is null) throw new InvalidOperationException($"Overlay PS: {psErr?.AsString()}");
        vsErr?.Dispose();
        psErr?.Dispose();

        _vs = device.CreateVertexShader(vsBlob.AsSpan());
        _ps = device.CreatePixelShader(psBlob.AsSpan());
        _layout = device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 8, 0),
        ], vsBlob.AsSpan());
        vsBlob.Dispose();
        psBlob.Dispose();

        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blend = device.CreateBlendState(blend);
        _rasterizer = device.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    public void Draw(OverlayGeometry geometry, CameraSnapshot cam)
    {
        int vertexCount = geometry.FillTriangles.Count + geometry.Lines.Count * 6;
        if (vertexCount == 0) return;
        if (_staging.Length < vertexCount)
            _staging = new Vertex[int.Max(vertexCount, _staging.Length * 2)];

        double halfW = cam.ViewportWidth * cam.MetersPerPixel / 2.0;
        double halfH = cam.ViewportHeight * cam.MetersPerPixel / 2.0;
        int n = 0;

        foreach (var (x, y, rgba) in geometry.FillTriangles)
        {
            _staging[n++] = new Vertex
            {
                X = (float)((x - cam.CenterX) / halfW),
                Y = (float)((y - cam.CenterY) / halfH),
                Rgba = rgba,
            };
        }

        foreach (var (ax, ay, bx, by, rgba, widthPx) in geometry.Lines)
        {
            // Expand each segment to a screen-space quad of the requested pixel width.
            double dxPx = (bx - ax) / cam.MetersPerPixel;
            double dyPx = (by - ay) / cam.MetersPerPixel;
            double length = Math.Sqrt(dxPx * dxPx + dyPx * dyPx);
            if (length < 1e-6) continue;
            double px = -dyPx / length * widthPx / 2.0 * cam.MetersPerPixel;
            double py = dxPx / length * widthPx / 2.0 * cam.MetersPerPixel;

            Vertex V(double wx, double wy) => new()
            {
                X = (float)((wx - cam.CenterX) / halfW),
                Y = (float)((wy - cam.CenterY) / halfH),
                Rgba = rgba,
            };
            var v0 = V(ax + px, ay + py);
            var v1 = V(bx + px, by + py);
            var v2 = V(ax - px, ay - py);
            var v3 = V(bx - px, by - py);
            _staging[n++] = v0;
            _staging[n++] = v1;
            _staging[n++] = v2;
            _staging[n++] = v2;
            _staging[n++] = v1;
            _staging[n++] = v3;
        }

        EnsureVertexBuffer(n);
        var mapped = _context.Map(_vertexBuffer!, 0, MapMode.WriteDiscard);
        unsafe
        {
            fixed (Vertex* src = _staging)
                Buffer.MemoryCopy(src, (void*)mapped.DataPointer,
                    _vertexCapacity * sizeof(Vertex), n * sizeof(Vertex));
        }
        _context.Unmap(_vertexBuffer!, 0);

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(_layout);
        _context.IASetVertexBuffer(0, _vertexBuffer!, (uint)Marshal.SizeOf<Vertex>());
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.OMSetBlendState(_blend);
        _context.RSSetState(_rasterizer);
        _context.Draw((uint)n, 0);
    }

    private void EnsureVertexBuffer(int vertexCount)
    {
        if (_vertexBuffer is not null && _vertexCapacity >= vertexCount) return;
        _vertexBuffer?.Dispose();
        _vertexCapacity = int.Max(vertexCount, 4096);
        _vertexBuffer = _device.CreateBuffer(new BufferDescription(
            (uint)(_vertexCapacity * Marshal.SizeOf<Vertex>()),
            BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    public void Dispose()
    {
        _vertexBuffer?.Dispose();
        _rasterizer.Dispose();
        _blend.Dispose();
        _layout.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
