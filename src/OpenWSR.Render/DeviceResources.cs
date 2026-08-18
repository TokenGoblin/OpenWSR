using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OpenWSR.Render;

/// <summary>D3D11 device, immediate context, and a flip-model swapchain on a child HWND.</summary>
public sealed class DeviceResources : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGISwapChain1 SwapChain { get; }
    public ID3D11RenderTargetView? BackBufferView { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public DeviceResources(IntPtr hwnd, int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0],
            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        Device = device!;
        Context = context!;

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore,
        };
        SwapChain = factory.CreateSwapChainForHwnd(Device, hwnd, desc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        CreateBackBufferView();
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        BackBufferView?.Dispose();
        BackBufferView = null;
        Context.Flush();
        SwapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        CreateBackBufferView();
    }

    private void CreateBackBufferView()
    {
        using var backBuffer = SwapChain.GetBuffer<ID3D11Texture2D>(0);
        BackBufferView = Device.CreateRenderTargetView(backBuffer);
    }

    public void Dispose()
    {
        BackBufferView?.Dispose();
        SwapChain.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
