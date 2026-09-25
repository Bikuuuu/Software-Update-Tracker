using System.Runtime.InteropServices;

namespace SoftwareUpdateTracker.App.Interop;

internal static class NativeMethods
{
    public const uint WM_APP = 0x8000;
    public const uint WM_NULL = 0x0000;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const int NIN_SELECT = 0x0400;
    public const int NIN_KEYSELECT = 0x0401;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;
    public const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    public const int SM_CXSMICON = 49;
    public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;

    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool UnregisterClassW(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] public static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    public const uint NORMAL_PRIORITY_CLASS = 0x20, BELOW_NORMAL_PRIORITY_CLASS = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool SetPriorityClass(nint process, uint priority);
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSizeEx(nint process, nint minimum, nint maximum, uint flags);
    [DllImport("kernel32.dll")] public static extern bool SetProcessInformation(nint process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string className, string? windowName);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
