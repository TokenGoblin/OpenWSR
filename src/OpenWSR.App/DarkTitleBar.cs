using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenWSR.App;

/// <summary>
/// Asks the desktop window manager for a dark title bar, so the native frame matches the
/// app's own chrome instead of sitting as a bright strip above it. Windows 10 2004+.
/// </summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        void Set(IntPtr hwnd)
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            Set(handle);
        else
            window.SourceInitialized += (_, _) => Set(new WindowInteropHelper(window).Handle);
    }
}
