using Microsoft.Win32;
using TinyTracker.Core;
using Windows.UI.Notifications;

namespace TinyTracker.App.Notifications;

// AppNotificationManager can't register in self-contained apps (WindowsAppSDK #6774), so use Windows.UI.Notifications directly.
internal sealed class ToastService
{
    private const string Aumid = AppInfo.InstanceKey;
    private const string KeyPath = @"Software\Classes\AppUserModelId\" + Aumid;

    public string? Register()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue("DisplayName", AppInfo.Name);
            key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "app-64.png"));
            return null;
        }
        catch (Exception ex)
        {
            return $"0x{ex.HResult:X8} {ex.Message}";
        }
    }

    // Clicks are handled in-process only, so toasts must not outlive the process.
    public void ClearHistory()
    {
        try { ToastNotificationManager.History.Clear(Aumid); } catch (Exception) { }
    }

    public static void RemoveRegistration()
    {
        try { ToastNotificationManager.History.Clear(Aumid); } catch (Exception) { }
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }
}
