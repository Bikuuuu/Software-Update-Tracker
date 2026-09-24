using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SoftwareUpdateTracker.App.Interop;
using SoftwareUpdateTracker.Core;

namespace SoftwareUpdateTracker.App;

public static class Program
{
    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (ProcessInfo.IsElevated())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Environment.ProcessPath}\"") { UseShellExecute = false });
            return 0;
        }

        if (Environment.GetCommandLineArgs().Contains("--cleanup-notifications"))
        {
            Notifications.ToastService.RemoveRegistration();
            return 0;
        }

        var instance = AppInstance.FindOrRegisterForKey(AppInfo.InstanceKey);
        if (!instance.IsCurrent)
        {
            NativeMethods.AllowSetForegroundWindow(instance.ProcessId);
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
        return 0;
    }
}
