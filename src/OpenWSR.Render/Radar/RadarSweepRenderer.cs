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
            // Draw only gates inside [filterMin, filterMax], in the palette's own units.
            // Applied here rather than by zeroing the palette's alpha so that moving the
            // control redraws the sweep already on screen, with no decode and no restage.
            float filterMin; float filterMax; float pad0; float pad1;
        };

        Buffer<float> edgeAzimuths : register(t1);
        // momentData holds value*valid and momentMask holds (valid, rangeFolded). Splitting
        // it this way is what lets the hardware filter the field at all: a sentinel cannot
        // leak into a weighted average that was multiplied out before it was uploaded, so
        // bilinear taps and every mip level reduce to a mean over the gates that actually
        // measured something, divided by how much of the footprint they covered.
        Texture2D<float> momentData : register(t0);
        Texture2D<float2> momentMask : register(t3);
        Texture2D palette : register(t2);
        SamplerState linearSamp : register(s1);
        SamplerState anisoSamp : register(s2);

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
            // uv.y walks the wedge from one radial boundary to the next rather than
            // sitting at the radial's centre for the whole wedge. Held constant, every
            // pixel of the wedge samples one texel row exactly, so no amount of linear
            // filtering can blend across azimuth — and the LOD derivative goes wrong at
            // the seam between two instances. Varying it puts the texel centre back at
            // the wedge centre and a 50/50 blend with the neighbour at each edge.
            o.uv = float2((float)k / gateCount, ((float)iid + (float)side) / radialCount);
            return o;
        }

        float4 PSMain(VSOut i) : SV_Target
        {
            // Both filtered fetches are taken before any branch. Sample needs screen-space
            // derivatives, and a texture fetch inside flow control has none to work from.
            float vp = momentData.Sample(anisoSamp, i.uv);
            float2 mask = momentMask.Sample(anisoSamp, i.uv);

            // Range folding is a display state rather than a measurement, so it is decided
            // once here and the same way whatever the smoothing slider says.
            if (mask.g > mask.r)
            {
                // Range folded: the NWS-style purple haze.
                float af = 0.85 * opacity * saturate(mask.g * 1.4);
                if (af < 0.004)
                    discard;
                return float4(float3(0.55, 0.25, 0.75) * af, af);
            }

            if (smoothing > 0.001)
            {
                // Variable-width Gaussian over the gate/radial grid, sentinel-aware:
                // invalid gates drop out of the weighted average instead of bleeding
                // in. smoothing 0..1 widens the kernel from near-point interpolation
                // to a soft consumer-style blur, and feathers echo edges by coverage.
                float2 tex = i.uv * float2(gateCount, radialCount) - 0.5;
                int2 c0 = int2(round(tex));
                float sigma = 0.45 + smoothing * 1.9;
                float inv2s2 = 1.0 / (2.0 * sigma * sigma);
                float acc = 0.0;
                float wsum = 0.0;
                float totalw = 0.0;
                [unroll]
                for (int dy = -2; dy <= 2; dy++)
                {
                    [unroll]
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int2 p = clamp(c0 + int2(dx, dy),
                            int2(0, 0), int2((int)gateCount - 1, (int)radialCount - 1));
                        float2 d = tex - float2(p);
                        float w = exp(-dot(d, d) * inv2s2);
                        totalw += w;
                        // value*valid and valid: an invalid gate contributes nothing to
                        // either sum, which is what the sentinel test used to do by hand.
                        acc += momentData.Load(int3(p, 0)) * w;
                        wsum += momentMask.Load(int3(p, 0)).r * w;
                    }
                }
                float coverage = wsum / max(totalw, 1e-6);
                if (coverage < 0.04)
                    discard; // isolated speckle fades out entirely
                float vg = acc / wsum;
                if (vg < filterMin || vg > filterMax)
                    discard;
                float t2 = saturate((vg - paletteMin) * invPaletteRange);
                float4 c2 = palette.Sample(linearSamp, float2(t2, 0.5));
                c2.a *= opacity * saturate(coverage * (1.6 - 0.4 * smoothing));
                if (c2.a < 0.004)
                    discard;
                return float4(c2.rgb * c2.a, c2.a);
            }

            // Hardware filtering: anisotropic over a mip chain. This is the floor rather
            // than a point sample because interpolating between two gates that both
            // measured something is interpolation, not invention — and because at wide
            // zoom a point sample picks one arbitrary gate out of the dozens under a
            // pixel, which is what makes the field crawl when the map is panned.
            float coverage = mask.r;
            if (coverage < 0.10)
                discard; // nothing measured under this pixel

            float v = vp / coverage;
            if (v < filterMin || v > filterMax)
                discard;
            float t = saturate((v - paletteMin) * invPaletteRange);
            float4 c = palette.Sample(linearSamp, float2(t, 0.5));
            // Feather the echo edge by how much of the footprint was measured. A hard
            // discard put a staircase on the boundary of every echo; this is the edge
            // antialiasing, and it costs nothing because coverage is already in hand.
            c.a *= opacity * saturate(coverage * 1.35);
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
        public float FilterMin, FilterMax, Pad0, Pad1;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _linearSampler;
    private readonly ID3D11SamplerState _anisoSampler;
    private readonly ID3D11BlendState _premultipliedBlend;
    private readonly ID3D11RasterizerState _rasterizer;

    private readonly Lock _pendingLock = new();
    private (SweepGeometry Geometry, byte[] Palette, float PalMin, float PalRange)? _pending;
    private bool _clearRequested;

    // Current GPU state
    private ID3D11ShaderResourceView? _dataView;
    private ID3D11Texture2D? _dataTexture;
    private ID3D11ShaderResourceView? _maskView;
    private ID3D11Texture2D? _maskTexture;
    private int _fieldGateCount, _fieldRadialCount;

    private ID3D11ShaderResourceView? _edgeView;
    private ID3D11Buffer? _edgeBuffer;
    private ID3D11ShaderResourceView? _paletteView;
    private ID3D11Texture2D? _paletteTexture;
    private SweepGeometry? _current;
    private float _paletteMin;
    private float _invPaletteRange;

    /// <summary>
    /// Value window, in the palette's units. Outside it a gate is not drawn at all.
    /// </summary>
    /// <remarks>
    /// Defaults to everything. The point of it is clear-air return: insects, birds and
    /// residual ground clutter sit between roughly 5 and 20 dBZ and can cover more of the
    /// screen than the weather does, and whether that is context worth seeing or clutter
    /// worth hiding depends on what someone is looking for.
    /// </remarks>
    public float FilterMin { get; set; } = float.NegativeInfinity;

    public float FilterMax { get; set; } = float.PositiveInfinity;

    public float Opacity { get; set; } = 0.85f;

    /// <summary>Smoothing strength 0–1: raw gates → soft consumer-style blur.</summary>
    public float Smoothing { get; set; }

    public double LastUploadMs { get; private set; }

    public RadarSweepRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        _vs = device.CreateVertexShader(Compile("VSMain", "vs_5_0"));
        _ps = device.CreatePixelShader(Compile("PSMain", "ps_5_0"));
        _constants = device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<SweepConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));

        _linearSampler = device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp));

        // Anisotropic rather than plain trilinear because the footprint of a pixel in
        // gate/radial space is wildly elongated: near the radar a pixel spans many radials
        // and a fraction of a gate, far out the reverse. An isotropic filter has to pick a
        // mip for the worse axis and blurs the other one away with it. MaxLOD is left open
        // so the chain can run out at national zoom, which is the case it exists for.
        _anisoSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 16,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0f,
            MaxLOD = float.MaxValue,
        });

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

    /// <summary>Compile both shaders without a device, so a syntax error is a failing test.</summary>
    public static void ValidateShaders()
    {
        Compile("VSMain", "vs_5_0");
        Compile("PSMain", "ps_5_0");
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
            FilterMin = FilterMin,
            FilterMax = FilterMax,
            Opacity = Opacity,
            GateCount = g.GateCount,
            RadialCount = g.RadialCount,
            Smoothing = Smoothing,
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
        _context.PSSetShaderResource(3, _maskView!);
        _context.PSSetSampler(1, _linearSampler);
        _context.PSSetSampler(2, _anisoSampler);
        _context.OMSetBlendState(_premultipliedBlend);
        _context.RSSetState(_rasterizer);

        _context.DrawInstanced((uint)((g.GateCount + 1) * 2), (uint)g.RadialCount, 0, 0);
    }

    private unsafe void Upload((SweepGeometry Geometry, byte[] Palette, float PalMin, float PalRange) staged)
    {
        var sw = Stopwatch.StartNew();
        var g = staged.Geometry;

        // Everything except the field textures is small and rebuilt per sweep.
        _edgeView?.Dispose(); _edgeView = null;
        _edgeBuffer?.Dispose(); _edgeBuffer = null;
        _paletteView?.Dispose(); _paletteView = null;
        _paletteTexture?.Dispose(); _paletteTexture = null;
        _current = null;

        // Allocating a mip chain is the expensive part of an upload — measured at 40 ms a
        // sweep when it was done every time, which an archive loop pays on every frame.
        // Consecutive sweeps of a site are almost always the same shape, so the textures
        // are kept and only their contents replaced.
        if (_dataTexture is null || _fieldGateCount != g.GateCount || _fieldRadialCount != g.RadialCount)
        {
            ReleaseFieldTextures();

                // MipLevels 0 asks for the full chain, which rules out an immutable texture:
            // the levels below the first are written by GenerateMips, not uploaded.
            _dataTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)g.GateCount,
                Height = (uint)g.RadialCount,
                MipLevels = 0,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            _dataView = _device.CreateShaderResourceView(_dataTexture);

            _maskTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)g.GateCount,
                Height = (uint)g.RadialCount,
                MipLevels = 0,
                ArraySize = 1,
                Format = Format.R8G8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            _maskView = _device.CreateShaderResourceView(_maskTexture);

            _fieldGateCount = g.GateCount;
            _fieldRadialCount = g.RadialCount;
        }

        fixed (float* p = g.Values)
            _context.UpdateSubresource(_dataTexture, 0, null, (IntPtr)p, (uint)(g.GateCount * 4), 0);
        fixed (byte* p = g.Mask)
            _context.UpdateSubresource(_maskTexture, 0, null, (IntPtr)p, (uint)(g.GateCount * 2), 0);

        // A box filter over the premultiplied pair is exactly the weighted average wanted
        // at every level, so the hardware can build the chain and the render thread does
        // not have to.
        _context.GenerateMips(_dataView);
        _context.GenerateMips(_maskView);

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

    private void ReleaseFieldTextures()
    {
        _dataView?.Dispose(); _dataView = null;
        _dataTexture?.Dispose(); _dataTexture = null;
        _maskView?.Dispose(); _maskView = null;
        _maskTexture?.Dispose(); _maskTexture = null;
        _fieldGateCount = _fieldRadialCount = 0;
    }

    private void ReleaseSweepResources()
    {
        ReleaseFieldTextures();
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
        _anisoSampler.Dispose();
        _constants.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
