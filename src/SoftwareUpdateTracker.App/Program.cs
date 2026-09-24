using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SoftwareUpdateTracker.App.Interop;
using SoftwareUpdateTracker.Core;
using SoftwareUpdateTracker.Core.Launch;

namespace SoftwareUpdateTracker.App;

public static class Program
{
    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // Maintenance verbs run as-is, even elevated (the uninstaller runs elevated).
        if (LaunchPolicy.IsMaintenanceVerb(args))
        {
            Notifications.ToastService.RemoveRegistration();
            return 0;
        }

        if (LaunchPolicy.ShouldRelaunchUnelevated(ProcessInfo.GetElevationType()))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Environment.ProcessPath}\"") { UseShellExecute = false });
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
