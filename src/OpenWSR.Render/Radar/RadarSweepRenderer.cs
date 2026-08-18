using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OpenWSR.Render.Radar;

/// <summary>
/// Draws one sweep. All geometry comes from SV_VertexID/SV_InstanceID: each radial is an
/// instanced triangle strip along range, positioned by the beam-path + great-circle math
/// in the vertex shader relative to the radar origin (jitter-free at any zoom). Moment
/// values live in an R32_FLOAT texture; switching products or palettes is a texture swap.
/// </summary>
public sealed class RadarSweepRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer SweepConstants : register(b0)
        {
            float2 originNdc;    // radar origin relative to camera, in NDC
            float2 invHalf;      // 1 / (half viewport in meters)
            float sinLat0; float cosLat0; float mercYRef; float lon0;
            float firstGateM; float gateSpacingM; float kea; float invEarthR;
            float sinElev; float cosElev; float paletteMin; float invPaletteRange;
            float opacity; float gateCount; float radialCount; float smoothing;
        };

        Buffer<float> edgeAzimuths : register(t1);
        Texture2D<float> momentData : register(t0);
        Texture2D palette : register(t2);
        SamplerState pointSamp : register(s0);
        SamplerState linearSamp : register(s1);

        static const float R_MERC = 6378137.0;
        static const float QUARTER_PI = 0.78539816339;

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID)
        {
            uint k = vid >> 1;
            uint side = vid & 1;

            float az = edgeAzimuths[iid + side];
            float r = max(0.0, firstGateM + ((float)k - 0.5) * gateSpacingM);

            // 4/3 effective earth radius beam propagation
            float h = sqrt(r * r + kea * kea + 2.0 * r * kea * sinElev) - kea;
            float s = kea * asin(r * cosElev / (kea + h));

            // Great-circle offset from the radar
            float delta = s * invEarthR;
            float sinD = sin(delta);
            float cosD = cos(delta);
            float sinLat = sinLat0 * cosD + cosLat0 * sinD * cos(az);
            float lat = asin(sinLat);
            float dLon = atan2(sin(az) * sinD * cosLat0, cosD - sinLat0 * sinLat);

            // Web Mercator, relative to the radar origin (small numbers, float-safe)
            float relX = R_MERC * dLon;
            float relY = R_MERC * (log(tan(QUARTER_PI + lat * 0.5)) - mercYRef);

            VSOut o;
            o.pos = float4(originNdc + float2(relX, relY) * invHalf, 0.0, 1.0);
            o.uv = float2((float)k / gateCount, ((float)iid + 0.5) / radialCount);
            return o;
        }

        float4 PSMain(VSOut i) : SV_Target
        {
            float v = momentData.Sample(pointSamp, i.uv);
            if (v < -1.5e30)
            {
                // Range folded: the NWS-style purple haze
                float a = 0.85 * opacity;
                return float4(float3(0.55, 0.25, 0.75) * a, a);
            }

            if (smoothing > 0.5)
            {
                // Sentinel-aware bilinear over the 2x2 texel neighborhood: invalid
                // gates drop out of the weighted average instead of bleeding in.
                float2 tex = i.uv * float2(gateCount, radialCount) - 0.5;
                float2 f = frac(tex);
                int2 p0 = int2(floor(tex));
                float acc = 0.0;
                float wsum = 0.0;
                [unroll]
                for (int dy = 0; dy < 2; dy++)
                {
                    [unroll]
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int2 p = clamp(p0 + int2(dx, dy),
                            int2(0, 0), int2((int)gateCount - 1, (int)radialCount - 1));
                        float s = momentData.Load(int3(p, 0));
                        if (s < -0.5e30) continue;
                        float w = (dx == 1 ? f.x : 1.0 - f.x) * (dy == 1 ? f.y : 1.0 - f.y);
                        acc += s * w;
                        wsum += w;
                    }
                }
                if (wsum < 0.12)
                    discard; // isolated speckle fades out entirely
                v = acc / wsum;
                float t2 = saturate((v - paletteMin) * invPaletteRange);
                float4 c2 = palette.Sample(linearSamp, float2(t2, 0.5));
                c2.a *= opacity * saturate(wsum * 1.6); // feather the echo edge
                if (c2.a < 0.004)
                    discard;
                return float4(c2.rgb * c2.a, c2.a);
            }

            if (v < -0.5e30)
                discard; // below threshold

            float t = saturate((v - paletteMin) * invPaletteRange);
            float4 c = palette.Sample(linearSamp, float2(t, 0.5));
            c.a *= opacity;
            if (c.a < 0.004)
                discard;
            return float4(c.rgb * c.a, c.a); // premultiplied
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct SweepConstants
    {
        public float OriginNdcX, OriginNdcY, InvHalfX, InvHalfY;
        public float SinLat0, CosLat0, MercYRef, Lon0;
        public float FirstGateM, GateSpacingM, Kea, InvEarthR;
        public float SinElev, CosElev, PaletteMin, InvPaletteRange;
        public float Opacity, GateCount, RadialCount, Smoothing;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _pointSampler;
    private readonly ID3D11SamplerState _linearSampler;
    private readonly ID3D11BlendState _premultipliedBlend;
    private readonly ID3D11RasterizerState _rasterizer;

    private readonly Lock _pendingLock = new();
    private (SweepGeometry Geometry, byte[] Palette, float PalMin, float PalRange)? _pending;
    private bool _clearRequested;

    // Current GPU state
    private ID3D11ShaderResourceView? _dataView;
    private ID3D11Texture2D? _dataTexture;
    private ID3D11ShaderResourceView? _edgeView;
    private ID3D11Buffer? _edgeBuffer;
    private ID3D11ShaderResourceView? _paletteView;
    private ID3D11Texture2D? _paletteTexture;
    private SweepGeometry? _current;
    private float _paletteMin;
    private float _invPaletteRange;

    public float Opacity { get; set; } = 0.85f;

    /// <summary>Sentinel-aware bilinear smoothing (the consumer-app radar look).</summary>
    public bool Smoothing { get; set; }

    public double LastUploadMs { get; private set; }

    public RadarSweepRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        _vs = device.CreateVertexShader(Compile("VSMain", "vs_5_0"));
        _ps = device.CreatePixelShader(Compile("PSMain", "ps_5_0"));
        _constants = device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<SweepConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));

        _pointSampler = device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp));
        _linearSampler = device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp));

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
        _premultipliedBlend = device.CreateBlendState(blend);
        _rasterizer = device.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    private static byte[] Compile(string entry, string profile)
    {
        Compiler.Compile(ShaderSource, entry, "RadarSweepRenderer", profile, out var blob, out var errors);
        if (blob is null)
            throw new InvalidOperationException($"Shader compile failed ({entry}): {errors?.AsString()}");
        errors?.Dispose();
        var bytes = blob.AsBytes();
        blob.Dispose();
        return bytes;
    }

    /// <summary>Stage a sweep (any thread); the render thread uploads it next frame.</summary>
    public void SetSweep(SweepGeometry geometry, byte[] palette256, float paletteMin, float paletteRange)
    {
        lock (_pendingLock)
        {
            _pending = (geometry, palette256, paletteMin, paletteRange);
            _clearRequested = false;
        }
    }

    public void Clear()
    {
        lock (_pendingLock)
        {
            _pending = null;
            _clearRequested = true;
        }
    }

    /// <summary>Render-thread: apply staged sweep, then draw if a sweep is loaded.</summary>
    public unsafe void Draw(CameraSnapshot cam)
    {
        (SweepGeometry, byte[], float, float)? staged = null;
        bool clear;
        lock (_pendingLock)
        {
            if (_pending is not null)
            {
                staged = _pending;
                _pending = null;
            }
            clear = _clearRequested;
            _clearRequested = false;
        }
        if (clear) ReleaseSweepResources();
        if (staged is not null) Upload(staged.Value);
        if (_current is null) return;

        var g = _current;
        double radarMercX = OpenWSR.Geo.GeoMath.ToMercator(g.RadarLatDeg, g.RadarLonDeg).X;
        double radarMercY = OpenWSR.Geo.GeoMath.ToMercator(g.RadarLatDeg, g.RadarLonDeg).Y;
        double halfW = cam.ViewportWidth * cam.MetersPerPixel / 2.0;
        double halfH = cam.ViewportHeight * cam.MetersPerPixel / 2.0;

        double lat0 = g.RadarLatDeg * Math.PI / 180.0;
        var constants = new SweepConstants
        {
            OriginNdcX = (float)((radarMercX - cam.CenterX) / halfW),
            OriginNdcY = (float)((radarMercY - cam.CenterY) / halfH),
            InvHalfX = (float)(1.0 / halfW),
            InvHalfY = (float)(1.0 / halfH),
            SinLat0 = (float)Math.Sin(lat0),
            CosLat0 = (float)Math.Cos(lat0),
            MercYRef = (float)Math.Log(Math.Tan(Math.PI / 4.0 + lat0 / 2.0)),
            Lon0 = (float)(g.RadarLonDeg * Math.PI / 180.0),
            FirstGateM = g.FirstGateM,
            GateSpacingM = g.GateSpacingM,
            Kea = (float)OpenWSR.Geo.GeoMath.EffectiveEarthRadiusM,
            InvEarthR = (float)(1.0 / OpenWSR.Geo.GeoMath.EarthRadiusM),
            SinElev = MathF.Sin(g.ElevationRad),
            CosElev = MathF.Cos(g.ElevationRad),
            PaletteMin = _paletteMin,
            InvPaletteRange = _invPaletteRange,
            Opacity = Opacity,
            GateCount = g.GateCount,
            RadialCount = g.RadialCount,
            Smoothing = Smoothing ? 1f : 0f,
        };
        _context.UpdateSubresource(constants, _constants);

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetConstantBuffer(0, _constants);
        _context.VSSetShaderResource(1, _edgeView!);
        _context.PSSetShaderResource(0, _dataView!);
        _context.PSSetShaderResource(2, _paletteView!);
        _context.PSSetSampler(0, _pointSampler);
        _context.PSSetSampler(1, _linearSampler);
        _context.OMSetBlendState(_premultipliedBlend);
        _context.RSSetState(_rasterizer);

        _context.DrawInstanced((uint)((g.GateCount + 1) * 2), (uint)g.RadialCount, 0, 0);
    }

    private unsafe void Upload((SweepGeometry Geometry, byte[] Palette, float PalMin, float PalRange) staged)
    {
        var sw = Stopwatch.StartNew();
        ReleaseSweepResources();
        var g = staged.Geometry;

        fixed (float* p = g.Data)
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)g.GateCount,
                Height = (uint)g.RadialCount,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            };
            _dataTexture = _device.CreateTexture2D(desc, [new SubresourceData((IntPtr)p, (uint)(g.GateCount * 4))]);
            _dataView = _device.CreateShaderResourceView(_dataTexture);
        }

        fixed (float* p = g.EdgeAzimuthsRad)
        {
            _edgeBuffer = _device.CreateBuffer(
                new BufferDescription((uint)(g.EdgeAzimuthsRad.Length * 4), BindFlags.ShaderResource,
                    ResourceUsage.Immutable),
                (IntPtr)p);
            _edgeView = _device.CreateShaderResourceView(_edgeBuffer, new ShaderResourceViewDescription
            {
                Format = Format.R32_Float,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)g.EdgeAzimuthsRad.Length },
            });
        }

        fixed (byte* p = staged.Palette)
        {
            var desc = new Texture2DDescription
            {
                Width = 256,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            };
            _paletteTexture = _device.CreateTexture2D(desc, [new SubresourceData((IntPtr)p, 256 * 4)]);
            _paletteView = _device.CreateShaderResourceView(_paletteTexture);
        }

        _paletteMin = staged.PalMin;
        _invPaletteRange = 1f / staged.PalRange;
        _current = g;
        LastUploadMs = sw.Elapsed.TotalMilliseconds;
    }

    private void ReleaseSweepResources()
    {
        _dataView?.Dispose(); _dataView = null;
        _dataTexture?.Dispose(); _dataTexture = null;
        _edgeView?.Dispose(); _edgeView = null;
        _edgeBuffer?.Dispose(); _edgeBuffer = null;
        _paletteView?.Dispose(); _paletteView = null;
        _paletteTexture?.Dispose(); _paletteTexture = null;
        _current = null;
    }

    public void Dispose()
    {
        ReleaseSweepResources();
        _rasterizer.Dispose();
        _premultipliedBlend.Dispose();
        _linearSampler.Dispose();
        _pointSampler.Dispose();
        _constants.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
