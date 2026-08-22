using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OpenWSR.Render.Radar;

/// <summary>
/// The volume, resampled onto a Cartesian grid and uploaded as a 3D texture, ready to draw.
/// Built on the render thread from a <c>VolumeGrid</c> the app layer produced; this assembly
/// deliberately knows nothing about sweeps or beam geometry.
/// </summary>
public sealed record VolumeUpload(
    byte[] Voxels, int Nx, int Ny, int Nz,
    double HalfWidthM, double BaseHeightM, double TopHeightM,
    byte[] PaletteRgba256, float PaletteMin, float PaletteMax,
    string Title);

/// <summary>
/// Draws a radar volume by ray marching.
///
/// Every pixel casts a ray through the box and accumulates colour front to back until it
/// is opaque. That is the honest way to show a volume: no threshold has to be chosen in
/// advance, so weak echo stays visible as haze rather than disappearing, and a strong core
/// is solid because a lot of it was integrated rather than because a rule said so. The
/// alternative — extracting an isosurface at some dBZ — draws a confident shell around a
/// number nobody picked for a reason.
///
/// Geometry comes entirely from SV_VertexID: one oversized triangle covering the screen,
/// no vertex buffers, matching the rest of the renderers here.
/// </summary>
public sealed class VolumeRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer VolumeConstants : register(b0)
        {
            float4 eye;        // xyz eye position, w tan(fov/2)
            float4 forward;    // xyz, w aspect
            float4 right;      // xyz, w step count
            float4 up;         // xyz, w overall opacity
            float4 boxMin;     // xyz metres, w alpha floor (normalised)
            float4 boxMax;     // xyz metres, w alpha ramp width (normalised)
            float4 misc;       // x density, y ring spacing m, z ring width m, w disc radius m
            float4 misc2;      // x ground z, y pixel angle, z cells across, w unused
        };

        Texture3D<float> volume : register(t0);
        Texture2D palette : register(t1);
        SamplerState linearSamp : register(s0);

        struct VSOut { float4 pos : SV_Position; float2 ndc : TEXCOORD0; };

        // One triangle big enough to cover the screen; cheaper than a quad and with no
        // seam down the diagonal.
        VSOut VSMain(uint vid : SV_VertexID)
        {
            float2 p = float2((vid == 2) ? 3.0 : -1.0, (vid == 1) ? 3.0 : -1.0);
            VSOut o;
            o.pos = float4(p, 0.0, 1.0);
            o.ndc = p;
            return o;
        }

        float4 PSMain(VSOut i) : SV_Target
        {
            float3 dir = normalize(
                forward.xyz
                + right.xyz * (i.ndc.x * eye.w * forward.w)
                + up.xyz    * (i.ndc.y * eye.w));

            // Slab intersection with the grid box.
            float3 sgn = step(0.0, dir) * 2.0 - 1.0;      // +1 or -1, never 0
            float3 invDir = sgn / max(abs(dir), 1e-9);
            float3 t0 = (boxMin.xyz - eye.xyz) * invDir;
            float3 t1 = (boxMax.xyz - eye.xyz) * invDir;
            float3 tNear = min(t0, t1);
            float3 tFar  = max(t0, t1);
            float tEnter = max(max(tNear.x, tNear.y), tNear.z);
            float tExit  = min(min(tFar.x, tFar.y), tFar.z);
            tEnter = max(tEnter, 0.0);

            float4 acc = float4(0, 0, 0, 0);

            if (tExit > tEnter)
            {
                float steps = right.w;
                float dt = (tExit - tEnter) / steps;
                // Alpha per step is scaled by how far the step covered, so the picture does
                // not get denser just because the camera moved closer and the steps got
                // shorter. Reference length is one grid cell.
                float cell = (boxMax.x - boxMin.x) / max(misc2.z, 1.0);
                float stepScale = dt / max(cell, 1.0);

                float3 boxSpan = boxMax.xyz - boxMin.xyz;

                // Start each ray at a different fraction of a step. Marching every ray from
                // the same place makes the step size visible as bands across the storm —
                // regular structure the radar never measured. Offsetting per pixel turns
                // that into fine noise, which the eye reads as texture instead of as data.
                float jitter = frac(sin(dot(i.pos.xy, float2(12.9898, 78.233))) * 43758.5453);

                [loop]
                for (float s = jitter; s < steps; s += 1.0)
                {
                    float3 p = eye.xyz + dir * (tEnter + dt * s);
                    float3 uvw = (p - boxMin.xyz) / boxSpan;

                    float v = volume.SampleLevel(linearSamp, uvw, 0);
                    // Zero is "no beam came here", not "very weak". Everything below the
                    // alpha floor is treated the same way, which also disposes of the haze
                    // linear filtering leaves along the edge of an echo.
                    if (v <= boxMin.w) continue;

                    float a = saturate((v - boxMin.w) / max(boxMax.w, 1e-5));
                    a = 1.0 - pow(abs(1.0 - a * misc.x), stepScale);
                    a *= up.w;
                    if (a <= 0.0) continue;

                    float3 col = palette.SampleLevel(linearSamp, float2(v, 0.5), 0).rgb;
                    acc.rgb += (1.0 - acc.a) * col * a;
                    acc.a   += (1.0 - acc.a) * a;

                    if (acc.a > 0.995) break;
                }
            }

            // The ground, drawn behind whatever the volume left transparent. Without a
            // surface underneath, a storm floats in a void and there is no telling how big
            // it is or which way it is moving.
            if (acc.a < 0.995 && abs(dir.z) > 1e-9)
            {
                float tGround = (misc2.x - eye.z) / dir.z;
                if (tGround > 0.0)
                {
                    float3 g = eye.xyz + dir * tGround;
                    float r = length(g.xy);
                    if (r <= misc.w)
                    {
                        float3 ground = float3(0.13, 0.15, 0.18);

                        // Range rings, widened with distance so they stay a line rather
                        // than dissolving into aliasing near the horizon.
                        float w = max(misc.z, tGround * misc2.y * 1.5);
                        float d = abs(r - round(r / misc.y) * misc.y);
                        float ring = 1.0 - smoothstep(0.0, w, d);
                        ground = lerp(ground, float3(0.34, 0.40, 0.48), ring * 0.85);

                        // The north line, so the view is orientable at a glance.
                        float northD = abs(g.x);
                        if (g.y > 0.0)
                        {
                            float north = 1.0 - smoothstep(0.0, w, northD);
                            ground = lerp(ground, float3(0.55, 0.62, 0.72), north * 0.8);
                        }

                        // Fade the far edge of the disc out rather than ending it on a
                        // hard rim, which reads as a wall.
                        float edge = 1.0 - smoothstep(misc.w * 0.88, misc.w, r);
                        acc.rgb += (1.0 - acc.a) * ground * edge;
                        acc.a   += (1.0 - acc.a) * edge;
                    }
                }
            }

            if (acc.a < 0.002) discard;
            return acc;   // premultiplied
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeConstants
    {
        public float EyeX, EyeY, EyeZ, TanHalfFov;
        public float FwdX, FwdY, FwdZ, Aspect;
        public float RightX, RightY, RightZ, Steps;
        public float UpX, UpY, UpZ, Opacity;
        public float BoxMinX, BoxMinY, BoxMinZ, AlphaFloor;
        public float BoxMaxX, BoxMaxY, BoxMaxZ, AlphaRange;
        public float Density, RingSpacingM, RingWidthM, DiscRadiusM;
        public float GroundZ, PixelAngle, CellsAcross, Pad1;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _linearSampler;
    private readonly ID3D11BlendState _premultipliedBlend;
    private readonly ID3D11RasterizerState _rasterizer;

    private readonly Lock _pendingLock = new();
    private VolumeUpload? _pending;
    private bool _clearRequested;

    private ID3D11Texture3D? _volumeTexture;
    private ID3D11ShaderResourceView? _volumeView;
    private ID3D11Texture2D? _paletteTexture;
    private ID3D11ShaderResourceView? _paletteView;
    private VolumeUpload? _current;

    /// <summary>
    /// Vertical stretch. A storm twelve kilometres tall inside a three-hundred-kilometre
    /// box is a smear at true scale; every 3D radar display in existence exaggerates the
    /// vertical, and the honest thing is to say so on screen rather than to pretend.
    /// </summary>
    public float VerticalExaggeration { get; set; } = 6f;

    /// <summary>How readily a voxel accumulates opacity. Higher is a denser, waxier storm.</summary>
    public float Density { get; set; } = 0.30f;

    public float Opacity { get; set; } = 1f;

    /// <summary>
    /// Physical value below which nothing is drawn at all — the display threshold. Set in
    /// the moment's own units so it means something to a forecaster.
    /// </summary>
    public float ThresholdValue { get; set; } = 15f;

    /// <summary>Ramp width above the threshold over which a voxel reaches full weight.</summary>
    public float ThresholdRampValue { get; set; } = 25f;

    public int Steps { get; set; } = 192;

    public double RingSpacingM { get; set; } = 50_000;

    public bool HasVolume => _current is not null;

    /// <summary>The extent the camera should frame, in metres, or null when nothing is loaded.</summary>
    public (Vector3 Centre, double Radius)? Bounds
    {
        get
        {
            if (_current is null) return null;
            float top = (float)((_current.TopHeightM - _current.BaseHeightM) * VerticalExaggeration);
            return (new Vector3(0, 0, top * 0.35f), _current.HalfWidthM);
        }
    }

    public VolumeRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        _vs = device.CreateVertexShader(Compile("VSMain", "vs_5_0"));
        _ps = device.CreatePixelShader(Compile("PSMain", "ps_5_0"));
        _constants = device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<VolumeConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));

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

    /// <summary>
    /// Compile both entry points and throw if either fails. Shader compilation happens at
    /// run time, on the render thread, where an exception becomes a black window rather
    /// than a message — so this exists to be called from a test instead.
    /// </summary>
    public static void ValidateShaders()
    {
        Compile("VSMain", "vs_5_0");
        Compile("PSMain", "ps_5_0");
    }

    private static byte[] Compile(string entry, string profile)
    {
        Compiler.Compile(ShaderSource, entry, "VolumeRenderer", profile, out var blob, out var errors);
        if (blob is null)
            throw new InvalidOperationException($"Shader compile failed ({entry}): {errors?.AsString()}");
        errors?.Dispose();
        var bytes = blob.AsBytes();
        blob.Dispose();
        return bytes;
    }

    /// <summary>Stage a volume from any thread; the render thread uploads it next frame.</summary>
    public void SetVolume(VolumeUpload upload)
    {
        lock (_pendingLock)
        {
            _pending = upload;
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

    /// <summary>
    /// Render thread: drop the volume and its textures now.
    ///
    /// <see cref="Clear"/> only stages the request, and it is serviced inside
    /// <see cref="Draw"/> — which stops being called the moment the view is left, so the
    /// staged release would never happen and several megabytes of 3D texture would stay
    /// resident for as long as the user was back on the map.
    /// </summary>
    public void ClearNow()
    {
        lock (_pendingLock)
        {
            _pending = null;
            _clearRequested = false;
        }
        Release();
    }

    /// <summary>Render thread: apply anything staged, then draw.</summary>
    public unsafe void Draw(VolumeCameraSnapshot cam, int viewportHeightPx)
    {
        VolumeUpload? staged;
        bool clear;
        lock (_pendingLock)
        {
            staged = _pending;
            _pending = null;
            clear = _clearRequested;
            _clearRequested = false;
        }
        if (clear) Release();
        if (staged is not null) Upload(staged);
        // The views are created together with _current, but stating it here is what lets
        // the nullable analysis agree, and it costs a branch that was going to happen anyway.
        if (_current is null || _volumeView is null || _paletteView is null) return;

        var v = _current;
        float halfW = (float)v.HalfWidthM;
        float top = (float)((v.TopHeightM - v.BaseHeightM) * VerticalExaggeration);

        // The threshold is set in the moment's units; the shader works in the normalised
        // 0..1 the texture holds, so it is converted once here rather than per sample.
        float span = MathF.Max(v.PaletteMax - v.PaletteMin, 1e-5f);
        float floorNorm = Math.Clamp((ThresholdValue - v.PaletteMin) / span, 0f, 1f);
        float rampNorm = MathF.Max(ThresholdRampValue / span, 1e-4f);

        var constants = new VolumeConstants
        {
            EyeX = cam.Eye.X, EyeY = cam.Eye.Y, EyeZ = cam.Eye.Z, TanHalfFov = cam.TanHalfFov,
            FwdX = cam.Forward.X, FwdY = cam.Forward.Y, FwdZ = cam.Forward.Z, Aspect = cam.Aspect,
            RightX = cam.Right.X, RightY = cam.Right.Y, RightZ = cam.Right.Z, Steps = Steps,
            UpX = cam.Up.X, UpY = cam.Up.Y, UpZ = cam.Up.Z, Opacity = Opacity,
            BoxMinX = -halfW, BoxMinY = -halfW, BoxMinZ = 0f, AlphaFloor = floorNorm,
            BoxMaxX = halfW, BoxMaxY = halfW, BoxMaxZ = top, AlphaRange = rampNorm,
            Density = Density,
            RingSpacingM = (float)RingSpacingM,
            RingWidthM = 250f,
            DiscRadiusM = halfW,
            GroundZ = 0f,
            PixelAngle = viewportHeightPx > 0 ? 2f * cam.TanHalfFov / viewportHeightPx : 1e-4f,
            // Step opacity is normalised against one grid cell, so the resolution has to
            // come from the grid that was actually built — it is a parameter of
            // VolumeGrid3D.Build, not a constant the renderer can assume.
            CellsAcross = v.Nx,
        };
        _context.UpdateSubresource(constants, _constants);

        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetShaderResource(0, _volumeView);
        _context.PSSetShaderResource(1, _paletteView);
        _context.PSSetSampler(0, _linearSampler);
        _context.OMSetBlendState(_premultipliedBlend);
        _context.RSSetState(_rasterizer);
        _context.Draw(3, 0);
    }

    private unsafe void Upload(VolumeUpload upload)
    {
        Release();

        fixed (byte* voxels = upload.Voxels)
        {
            var desc = new Texture3DDescription
            {
                Width = (uint)upload.Nx,
                Height = (uint)upload.Ny,
                Depth = (uint)upload.Nz,
                MipLevels = 1,
                Format = Format.R8_UNorm,
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
            };
            _volumeTexture = _device.CreateTexture3D(desc, [
                new SubresourceData((IntPtr)voxels, (uint)upload.Nx, (uint)(upload.Nx * upload.Ny)),
            ]);
            _volumeView = _device.CreateShaderResourceView(_volumeTexture);
        }

        fixed (byte* palette = upload.PaletteRgba256)
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
            _paletteTexture = _device.CreateTexture2D(desc, [
                new SubresourceData((IntPtr)palette, 256 * 4),
            ]);
            _paletteView = _device.CreateShaderResourceView(_paletteTexture);
        }

        _current = upload;
    }

    private void Release()
    {
        _volumeView?.Dispose();
        _volumeTexture?.Dispose();
        _paletteView?.Dispose();
        _paletteTexture?.Dispose();
        _volumeView = null;
        _volumeTexture = null;
        _paletteView = null;
        _paletteTexture = null;
        _current = null;
    }

    public void Dispose()
    {
        Release();
        _rasterizer.Dispose();
        _premultipliedBlend.Dispose();
        _linearSampler.Dispose();
        _constants.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
