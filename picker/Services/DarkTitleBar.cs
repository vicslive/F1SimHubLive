using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace F1SimHubLive.Picker.Services;

/// <summary>
/// Enables Windows' immersive dark title-bar chrome for a WPF Window so the
/// caption matches the rest of the app (which is already painted dark). Uses
/// DwmSetWindowAttribute with DWMWA_USE_IMMERSIVE_DARK_MODE (attribute 20 on
/// Windows 10 build 19041+ and Windows 11; the older attribute 19 is used as
/// a fallback for early Windows 10 builds). Silent no-op on older systems.
/// </summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>
    /// Call from a Window's constructor — hooks SourceInitialized so the
    /// HWND exists before we poke DWM.
    /// </summary>
    public static void Enable(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero) return;
            int useDark = 1;
            // Newer attribute first; non-zero return means unknown attribute
            // on this build, so try the legacy one.
            if (DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
            }
        };
    }
}
