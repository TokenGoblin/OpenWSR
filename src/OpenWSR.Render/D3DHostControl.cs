using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OpenWSR.Render;

/// <summary>
/// WPF <see cref="HwndHost"/> that creates a child HWND with its own D3D11 swapchain.
/// The child window always draws above WPF content in its rectangle (airspace); floating
/// UI over the map must be a Popup/ToolTip or drawn in the D3D scene.
/// Mouse input is handled in the child window's WndProc, not via WPF events.
/// </summary>
public sealed class D3DHostControl : HwndHost
{
    private const string WindowClassName = "OpenWSR.D3DSurface";
    private static readonly Win32.WndProcDelegate StaticWndProc = WndProc;
    private static readonly ConcurrentDictionary<IntPtr, D3DHostControl> Instances = new();
    private static bool _classRegistered;

    private readonly MapView _mapView;
    private IntPtr _hwnd;

    public D3DHostControl(MapView mapView)
    {
        _mapView = mapView;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        RegisterWindowClass();
        _hwnd = Win32.CreateWindowExW(
            0, WindowClassName, string.Empty,
            Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPSIBLINGS,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle, IntPtr.Zero, Win32.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");

        Instances[_hwnd] = this;
        var dpi = VisualTreeHelper.GetDpi(this);
        _mapView.Start(_hwnd,
            (int)(ActualWidth * dpi.DpiScaleX),
            (int)(ActualHeight * dpi.DpiScaleY));
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _mapView.Stop();
        Instances.TryRemove(hwnd.Handle, out _);
        Win32.DestroyWindow(hwnd.Handle);
    }

    private static void RegisterWindowClass()
    {
        if (_classRegistered) return;
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = Win32.CS_DBLCLKS,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticWndProc),
            hInstance = Win32.GetModuleHandleW(null),
            hCursor = Win32.LoadCursorW(IntPtr.Zero, 32512), // IDC_ARROW
            lpszClassName = WindowClassName,
        };
        if (Win32.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException(
                $"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}");
        _classRegistered = true;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Instances.TryGetValue(hWnd, out var self))
            return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
        var view = self._mapView;

        switch (msg)
        {
            case Win32.WM_SIZE:
            {
                var (w, h) = Win32.ClientPoint(lParam);
                view.Resize(w, h);
                return IntPtr.Zero;
            }
            case Win32.WM_PAINT:
                Win32.ValidateRect(hWnd, IntPtr.Zero);
                return IntPtr.Zero;
            case Win32.WM_ERASEBKGND:
                return 1; // the swapchain covers everything
            case Win32.WM_KEYDOWN:
                view.RaiseKeyPressed((int)(long)wParam);
                return IntPtr.Zero;
            case Win32.WM_LBUTTONDOWN:
            {
                Win32.SetFocus(hWnd); // keyboard focus follows the map click
                Win32.SetCapture(hWnd);
                var (x, y) = Win32.ClientPoint(lParam);
                view.OnMouseDown(x, y);
                return IntPtr.Zero;
            }
            case Win32.WM_MOUSEMOVE:
            {
                var (x, y) = Win32.ClientPoint(lParam);
                view.OnMouseMove(x, y);
                return IntPtr.Zero;
            }
            case Win32.WM_LBUTTONUP:
                Win32.ReleaseCapture();
                view.OnMouseUp();
                return IntPtr.Zero;
            case Win32.WM_MOUSEWHEEL:
            {
                // Wheel coordinates are screen-relative, unlike the other mouse messages.
                var pt = new Win32.POINT
                {
                    X = (short)((long)lParam & 0xFFFF),
                    Y = (short)(((long)lParam >> 16) & 0xFFFF),
                };
                Win32.ScreenToClient(hWnd, ref pt);
                view.OnMouseWheel(pt.X, pt.Y, Win32.WheelDelta(wParam));
                return IntPtr.Zero;
            }
            default:
                return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }
}
