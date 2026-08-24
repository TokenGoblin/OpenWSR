using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OpenWSR.Render;

/// <summary>Which ends of a segment get a round cap.</summary>
public enum LineCaps
{
    /// <summary>
    /// Flat at A, round at B. The default, because nearly every line here is one link of a
    /// chain — a polygon outline, a circle, a storm's past track. The round end sits on the
    /// shared vertex and covers the wedge the next segment's flat start leaves open, so a
    /// chain gets proper round joins with each cap drawn <em>once</em>. Rounding both ends
    /// instead would stack two half-discs on every shared vertex, and at any alpha below
    /// opaque that reads as a string of beads along the line.
    /// </summary>
    Joined,

    /// <summary>Round at both ends — for a segment that stands alone, such as a dash.</summary>
    Both,
}

/// <summary>
/// One straight segment in Mercator metres, stroked <see cref="WidthPx"/> screen pixels wide.
/// </summary>
/// <remarks>
/// A record struct rather than the tuple this used to be, so that <see cref="Caps"/> has
/// somewhere to live. The conversion keeps the tuple form working at the call sites that do
/// not care.
/// </remarks>
public readonly record struct OverlayLine(
    double Ax, double Ay, double Bx, double By, uint Rgba, float WidthPx,
    LineCaps Caps = LineCaps.Joined)
{
    public static implicit operator OverlayLine(
        (double Ax, double Ay, double Bx, double By, uint Rgba, float WidthPx) t) =>
        new(t.Ax, t.Ay, t.Bx, t.By, t.Rgba, t.WidthPx);
}

/// <summary>Overlay geometry kept in double-precision Mercator; transformed per frame.</summary>
public sealed class OverlayGeometry
{
    public List<(double X, double Y, uint Rgba)> FillTriangles { get; } = [];         // 3 entries per tri
    public List<OverlayLine> Lines { get; } = [];

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
    /// <remarks>
    /// Lines are drawn as a signed distance field rather than as a bare quad. Each vertex
    /// carries where it sits relative to its own segment — <c>local</c> is (along, across) in
    /// pixels with the segment running from 0 to <c>shape.x</c> — so the pixel shader can
    /// measure its true distance to the centreline and feather the last pixel of it.
    ///
    /// That distance is to the <em>segment</em>, not to the infinite line, which is what makes
    /// the ends round: past either endpoint the nearest point on the segment is the endpoint
    /// itself, so the contour closes as a semicircle for free. The quad is grown by the half
    /// width at each capped end to leave room for it.
    ///
    /// The alternative — a multisampled render target — costs bandwidth on every layer under
    /// this one to fix the jaggedness of the thinnest.
    /// </remarks>
    private const string ShaderSource = """
        struct VSIn
        {
            float2 pos   : POSITION;
            float4 color : COLOR0;
            float2 local : TEXCOORD0;   // (along, across) from this segment's A, in pixels
            float3 shape : TEXCOORD1;   // (length px, half width px, 1 if A is round)
        };
        struct VSOut
        {
            float4 pos   : SV_Position;
            float4 color : COLOR0;
            float2 local : TEXCOORD0;
            float3 shape : TEXCOORD1;
        };
        VSOut VSMain(VSIn i)
        {
            VSOut o;
            o.pos = float4(i.pos, 0.0, 1.0);
            o.color = i.color;
            o.local = i.local;
            o.shape = i.shape;
            return o;
        }
        float4 PSMain(VSOut i) : SV_Target
        {
            float alpha = i.color.a;
            if (i.shape.y > 0.0)  // a stroked segment; fills carry a zero half width
            {
                float pastB = max(0.0, i.local.x - i.shape.x);
                float pastA = max(0.0, -i.local.x) * i.shape.z;   // zero unless A is round
                float body = i.shape.y + 0.5 - length(float2(max(pastA, pastB), i.local.y));
                // A square end still wants a soft edge, so cut it with its own ramp rather
                // than by where the quad happens to stop.
                float squareA = i.shape.z > 0.5 ? 1e6 : i.local.x + 0.5;
                // A one-pixel ramp straddling the nominal edge: solid half a pixel inside,
                // clear half a pixel outside.
                alpha *= saturate(min(body, squareA));
            }
            return float4(i.color.rgb * alpha, alpha); // premultiplied
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public float X, Y;
        public uint Rgba;
        public float AlongPx, AcrossPx;
        public float LengthPx, HalfWidthPx, RoundStart;
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

    /// <summary>
    /// Physical pixels per device-independent unit, so that a width reads the same thickness
    /// on any display. The swap chain is sized in physical pixels, so on a 150 % screen every
    /// stroke was two thirds of its intended weight — which is most of why they looked like
    /// hairlines.
    /// </summary>
    public float DipScale { get; set; } = 1f;

    /// <summary>
    /// Compile both entry points and throw if either fails. Shader compilation happens at run
    /// time, on the render thread, where an exception becomes a black window rather than a
    /// message — so this exists to be called from a test instead.
    /// </summary>
    public static void ValidateShaders()
    {
        foreach (var (entry, profile) in new[] { ("VSMain", "vs_5_0"), ("PSMain", "ps_5_0") })
        {
            Compiler.Compile(ShaderSource, entry, "OverlayRenderer", profile,
                out var blob, out var errors);
            if (blob is null)
                throw new InvalidOperationException(
                    $"Shader compile failed ({entry}): {errors?.AsString()}");
            errors?.Dispose();
            blob.Dispose();
        }
    }

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
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32B32_Float, 20, 0),
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

        foreach (var (ax, ay, bx, by, rgba, widthPx, caps) in geometry.Lines)
        {
            // Expand each segment to a screen-space quad, oversized so the shader's distance
            // field has room to draw the round ends and the antialiasing ramp inside it.
            double dxPx = (bx - ax) / cam.MetersPerPixel;
            double dyPx = (by - ay) / cam.MetersPerPixel;
            double lengthPx = Math.Sqrt(dxPx * dxPx + dyPx * dyPx);
            if (lengthPx < 1e-6) continue;

            double halfWidthPx = widthPx * DipScale / 2.0;
            double bleed = halfWidthPx + 1.0;                 // cap radius plus the AA ramp
            double ux = dxPx / lengthPx, uy = dyPx / lengthPx;

            // Local axes in world units: along the segment, and 90 degrees off it.
            double alongX = ux * cam.MetersPerPixel, alongY = uy * cam.MetersPerPixel;
            double acrossX = -alongY, acrossY = alongX;

            Vertex V(double along, double across) => new()
            {
                X = (float)((ax + alongX * along + acrossX * across - cam.CenterX) / halfW),
                Y = (float)((ay + alongY * along + acrossY * across - cam.CenterY) / halfH),
                Rgba = rgba,
                AlongPx = (float)along,
                AcrossPx = (float)across,
                LengthPx = (float)lengthPx,
                HalfWidthPx = (float)halfWidthPx,
                RoundStart = caps == LineCaps.Both ? 1f : 0f,
            };
            var v0 = V(-bleed, bleed);
            var v1 = V(lengthPx + bleed, bleed);
            var v2 = V(-bleed, -bleed);
            var v3 = V(lengthPx + bleed, -bleed);
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
