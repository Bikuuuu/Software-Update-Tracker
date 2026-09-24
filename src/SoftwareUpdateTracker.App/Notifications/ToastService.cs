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
        _shown.Add(toast);
        ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
    }

    public static void RemoveRegistration()
    {
        try { ToastNotificationManager.History.Clear(Aumid); } catch (Exception) { }
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }
}
