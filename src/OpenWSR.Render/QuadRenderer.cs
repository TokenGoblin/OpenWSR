using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace OpenWSR.Render;

/// <summary>
/// Draws textured or solid-color screen-space quads (map tiles, markers) with geometry
/// generated from SV_VertexID — no vertex buffers anywhere.
/// </summary>
public sealed class QuadRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer PerQuad : register(b0)
        {
            float4 destRect; // clip space: x0, y0, x1, y1
            float4 uvRect;   // u0, vTop, u1, vBottom
            float4 tint;
            // Rotation about a clip-space pivot: (pivotX, pivotY, cos, sin). With cos = 1
            // and sin = 0 this is the identity, so unrotated callers pay nothing and need
            // no separate code path.
            float4 rot;
            // x holds the viewport aspect, so a rotation is square on screen rather than
            // sheared by the difference between clip units and pixels.
            float4 extra;
        };

        Texture2D tex : register(t0);
        SamplerState samp : register(s0);

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VSMain(uint vid : SV_VertexID)
        {
            float2 corner = float2(vid & 1, vid >> 1); // strip: (0,0)(1,0)(0,1)(1,1)
            VSOut o;
            float2 p = float2(
                lerp(destRect.x, destRect.z, corner.x),
                lerp(destRect.y, destRect.w, corner.y));

            float2 d = p - rot.xy;
            d.x *= extra.x;                                     // into square space
            d = float2(d.x * rot.z - d.y * rot.w,
                       d.x * rot.w + d.y * rot.z);
            d.x /= extra.x;                                     // back to clip space
            o.pos = float4(rot.xy + d, 0.0, 1.0);
            o.uv = float2(
                lerp(uvRect.x, uvRect.z, corner.x),
                lerp(uvRect.w, uvRect.y, corner.y)); // v flips: clip +y is up, texture v=0 is top
            return o;
        }

        float4 PSMain(VSOut i) : SV_Target
        {
            return tex.Sample(samp, i.uv) * tint;
        }

        float4 PSSolid(VSOut i) : SV_Target
        {
            return tint;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct PerQuad
    {
        public float X0, Y0, X1, Y1;
        public float U0, V0, U1, V1;
        public float R, G, B, A;
        public float PivotX, PivotY, Cos, Sin;
        public float Aspect, Pad0, Pad1, Pad2;
    }

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psTextured;
    private readonly ID3D11PixelShader _psSolid;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _alphaBlend;
    private readonly ID3D11RasterizerState _rasterizer;

    public QuadRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;

        using var vsBlob = Compile("VSMain", "vs_5_0");
        using var psBlob = Compile("PSMain", "ps_5_0");
        using var psSolidBlob = Compile("PSSolid", "ps_5_0");
        _vs = device.CreateVertexShader(vsBlob.AsSpan());
        _psTextured = device.CreatePixelShader(psBlob.AsSpan());
        _psSolid = device.CreatePixelShader(psSolidBlob.AsSpan());

        _constants = device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<PerQuad>(), BindFlags.ConstantBuffer, ResourceUsage.Default));

        _sampler = device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp));

        var blend = BlendDescription.NonPremultiplied;
        _alphaBlend = device.CreateBlendState(blend);
        _rasterizer = device.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    private static Blob Compile(string entry, string profile)
    {
        Compiler.Compile(ShaderSource, entry, "QuadRenderer", profile, out var blob, out var errors);
        if (blob is null)
            throw new InvalidOperationException(
                $"Shader compile failed ({entry}): {errors?.AsString()}");
        errors?.Dispose();
        return blob;
    }

    public void Begin()
    {
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_vs);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetSampler(0, _sampler);
        _context.RSSetState(_rasterizer);
        _context.OMSetBlendState(_alphaBlend);
    }

    public void DrawTextured(
        (float X0, float Y0, float X1, float Y1) clipRect,
        (float U0, float V0, float U1, float V1) uvRect,
        ID3D11ShaderResourceView texture,
        float opacity = 1f,
        float tintR = 1f, float tintG = 1f, float tintB = 1f)
    {
        var data = new PerQuad
        {
            X0 = clipRect.X0, Y0 = clipRect.Y0, X1 = clipRect.X1, Y1 = clipRect.Y1,
            U0 = uvRect.U0, V0 = uvRect.V0, U1 = uvRect.U1, V1 = uvRect.V1,
            R = tintR, G = tintG, B = tintB, A = opacity,
            Cos = 1f, Sin = 0f, Aspect = 1f,
        };
        _context.UpdateSubresource(data, _constants);
        _context.PSSetShader(_psTextured);
        _context.PSSetShaderResource(0, texture);
        _context.Draw(4, 0);
    }

    /// <summary>
    /// A textured quad placed by its <em>hot spot</em> and turned about it.
    ///
    /// Placefile icons are pinned by a chosen pixel — a pin's tip, not its middle — and may
    /// carry a bearing. <paramref name="left"/> and <paramref name="top"/> are the quad's
    /// top-left corner relative to that anchor, in clip units.
    /// </summary>
    public void DrawTexturedRotated(
        float anchorX, float anchorY,
        float left, float top, float width, float height,
        (float U0, float V0, float U1, float V1) uvRect,
        ID3D11ShaderResourceView texture,
        float angleRadians, float aspect, float opacity = 1f)
    {
        var data = new PerQuad
        {
            X0 = anchorX + left,
            Y0 = anchorY + top - height,
            X1 = anchorX + left + width,
            Y1 = anchorY + top,
            U0 = uvRect.U0, V0 = uvRect.V0, U1 = uvRect.U1, V1 = uvRect.V1,
            R = 1f, G = 1f, B = 1f, A = opacity,
            PivotX = anchorX, PivotY = anchorY,
            // Clockwise on screen, which is how a bearing reads.
            Cos = MathF.Cos(angleRadians), Sin = -MathF.Sin(angleRadians),
            Aspect = aspect <= 0 ? 1f : aspect,
        };
        _context.UpdateSubresource(data, _constants);
        _context.PSSetShader(_psTextured);
        _context.PSSetShaderResource(0, texture);
        _context.Draw(4, 0);
    }

    public void DrawSolid((float X0, float Y0, float X1, float Y1) clipRect, float r, float g, float b, float a)
    {
        var data = new PerQuad
        {
            X0 = clipRect.X0, Y0 = clipRect.Y0, X1 = clipRect.X1, Y1 = clipRect.Y1,
            R = r, G = g, B = b, A = a,
            Cos = 1f, Sin = 0f, Aspect = 1f,
        };
        _context.UpdateSubresource(data, _constants);
        _context.PSSetShader(_psSolid);
        _context.Draw(4, 0);
    }

    public void Dispose()
    {
        _rasterizer.Dispose();
        _alphaBlend.Dispose();
        _sampler.Dispose();
        _constants.Dispose();
        _psSolid.Dispose();
        _psTextured.Dispose();
        _vs.Dispose();
    }
}
