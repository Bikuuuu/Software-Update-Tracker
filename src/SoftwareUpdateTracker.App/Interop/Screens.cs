using System.Runtime.InteropServices;
using SoftwareUpdateTracker.Core.Layout;

namespace SoftwareUpdateTracker.App.Interop;

internal static class Screens
{
    public static (PixelRect Work, uint Dpi) TaskbarMonitor()
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        var monitor = NativeMethods.MonitorFromWindow(taskbar, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfoW(monitor, ref info);
        NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var r = info.rcWork;
        return (new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), dpi);
    }
}
