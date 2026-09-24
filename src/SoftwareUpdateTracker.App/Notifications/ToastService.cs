using Microsoft.Win32;
using SoftwareUpdateTracker.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace SoftwareUpdateTracker.App.Notifications;

// AppNotificationManager can't register in self-contained apps (WindowsAppSDK #6774), so use Windows.UI.Notifications directly.
internal sealed class ToastService
{
    private const string Aumid = AppInfo.InstanceKey;
    private const string KeyPath = @"Software\Classes\AppUserModelId\" + Aumid;
    private readonly List<ToastNotification> _shown = [];

    public event EventHandler<string>? ActionInvoked;

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

    public void ShowTest()
    {
        var xml = new XmlDocument();
        xml.LoadXml("""
            <toast launch="action=open">
              <visual><binding template="ToastGeneric">
                <text>3 updates ready</text>
                <text>Logitech G HUB, NVIDIA App, Steam</text>
              </binding></visual>
              <actions>
                <action content="Update all" arguments="action=updateAll" />
                <action content="View" arguments="action=view" />
              </actions>
            </toast>
            """);
        var toast = new ToastNotification(xml);
        toast.Activated += (_, args) =>
        {
            var value = (args as ToastActivatedEventArgs)?.Arguments ?? "";
            ActionInvoked?.Invoke(this, value.StartsWith("action=", StringComparison.Ordinal) ? value[7..] : "open");
        };
        toast.Dismissed += (_, _) => Forget(toast);
        toast.Failed += (_, _) => Forget(toast);
        lock (_shown) _shown.Add(toast);
        ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
    }

    // Clicks are handled in-process only, so toasts must not outlive the process.
    public void ClearHistory()
    {
        try { ToastNotificationManager.History.Clear(Aumid); } catch (Exception) { }
        lock (_shown) _shown.Clear();
    }

    public static void RemoveRegistration()
    {
        try { ToastNotificationManager.History.Clear(Aumid); } catch (Exception) { }
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }

    private void Forget(ToastNotification toast)
    {
        lock (_shown) _shown.Remove(toast);
    }
}
