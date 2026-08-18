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
        };

        Texture2D tex : register(t0);
        SamplerState samp : register(s0);

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VSMain(uint vid : SV_VertexID)
        {
            float2 corner = float2(vid & 1, vid >> 1); // strip: (0,0)(1,0)(0,1)(1,1)
            VSOut o;
            o.pos = float4(
                lerp(destRect.x, destRect.z, corner.x),
                lerp(destRect.y, destRect.w, corner.y),
                0.0, 1.0);
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
        float opacity = 1f)
    {
        var data = new PerQuad
        {
            X0 = clipRect.X0, Y0 = clipRect.Y0, X1 = clipRect.X1, Y1 = clipRect.Y1,
            U0 = uvRect.U0, V0 = uvRect.V0, U1 = uvRect.U1, V1 = uvRect.V1,
            R = 1, G = 1, B = 1, A = opacity,
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
