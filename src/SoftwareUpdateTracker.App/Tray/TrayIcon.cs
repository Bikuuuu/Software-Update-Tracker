using System.Runtime.InteropServices;
using SoftwareUpdateTracker.App.Interop;

namespace SoftwareUpdateTracker.App.Tray;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = NativeMethods.WM_APP + 1;
    private const string ClassName = "SoftwareUpdateTracker.Tray";
    private static NativeMethods.WndProc? s_wndProc;

    private readonly uint _taskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
    private readonly nint _hwnd;
    private string _iconPath;
    private string _tooltip;
    private nint _icon;

    public TrayIcon(string iconPath, string tooltip)
    {
        _iconPath = iconPath;
        _tooltip = tooltip;
        s_wndProc = WndProc;
        var instance = NativeMethods.GetModuleHandleW(null);
        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = instance,
            lpszClassName = ClassName,
        };
        NativeMethods.RegisterClassExW(ref wc);
        // Hidden top-level window: message-only windows miss the TaskbarCreated broadcast.
        _hwnd = NativeMethods.CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0, 0, 0, instance, 0);
    }

    public event EventHandler? Activated;
    public event EventHandler<int>? MenuCommand;
    public event EventHandler? CloseRequested;

    public bool Added { get; private set; }
    public IReadOnlyList<(int Id, string Text)> MenuItems { get; set; } = [];

    public bool Show()
    {
        LoadIcon();
        Added = Notify(NativeMethods.NIM_ADD) || Notify(NativeMethods.NIM_MODIFY);
        if (Added)
        {
            var data = Data(0);
            data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
        }
        return Added;
    }

    public void Update(string iconPath, string tooltip)
    {
        _iconPath = iconPath;
        _tooltip = tooltip;
        LoadIcon();
        Notify(NativeMethods.NIM_MODIFY);
    }

    private NativeMethods.NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private bool Notify(uint message)
    {
        var data = Data(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        return NativeMethods.Shell_NotifyIconW(message, ref data);
    }

    private void LoadIcon()
    {
        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, NativeMethods.GetDpiForSystem());
        var next = NativeMethods.LoadImageW(0, _iconPath, NativeMethods.IMAGE_ICON, size, size, NativeMethods.LR_LOADFROMFILE);
        if (_icon != 0) NativeMethods.DestroyIcon(_icon);
        _icon = next;
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam & 0xFFFF))
            {
                case NativeMethods.NIN_SELECT or NativeMethods.NIN_KEYSELECT:
                    Activated?.Invoke(this, EventArgs.Empty);
                    break;
                case (int)NativeMethods.WM_CONTEXTMENU:
                    ShowMenu((short)(ushort)(wParam & 0xFFFF), (short)(ushort)((wParam >> 16) & 0xFFFF));
                    break;
            }
            return 0;
        }
        if (msg == _taskbarCreated)
        {
            Show();
            return 0;
        }
        if (msg == NativeMethods.WM_CLOSE)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return 0;
        }
        if (msg == NativeMethods.WM_DISPLAYCHANGE && Added)
        {
            LoadIcon();
            Notify(NativeMethods.NIM_MODIFY);
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = NativeMethods.CreatePopupMenu();
        foreach (var (id, text) in MenuItems)
        {
            if (text == "-") NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
            else NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (nuint)id, text);
        }
        NativeMethods.SetForegroundWindow(_hwnd);
        var command = NativeMethods.TrackPopupMenuEx(menu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN, x, y, _hwnd, 0);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, 0, 0);
        NativeMethods.DestroyMenu(menu);
        if (command != 0) MenuCommand?.Invoke(this, (int)command);
    }

    public void Dispose()
    {
        if (Added)
        {
            var data = Data(0);
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            Added = false;
        }
        if (_icon != 0) NativeMethods.DestroyIcon(_icon);
        NativeMethods.DestroyWindow(_hwnd);
        NativeMethods.UnregisterClassW(ClassName, NativeMethods.GetModuleHandleW(null));
    }
}
