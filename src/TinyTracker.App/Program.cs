using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using TinyTracker.App.Interop;
using TinyTracker.Core;
using TinyTracker.Core.Launch;

namespace TinyTracker.App;

public static class Program
{
    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // Maintenance verbs run as-is, even elevated (the uninstaller runs elevated).
        if (LaunchPolicy.IsMaintenanceVerb(args)) return Cleanup();

        if (LaunchPolicy.ShouldRelaunchUnelevated(ProcessInfo.GetElevationType()))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Environment.ProcessPath}\"") { UseShellExecute = false });
            return 0;
        }

        // A demo runs next to the real app, not instead of it.
        var instance = AppInstance.FindOrRegisterForKey(App.IsDemo(args) ? AppInfo.InstanceKey + ".Demo" : AppInfo.InstanceKey);
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

    // Removes what the app registered for this user. Every step runs even when another fails; a failure exits with 1.
    private static int Cleanup()
    {
        var failed = false;
        void Step(Action step)
        {
            try
            {
                step();
            }
            catch (Exception)
            {
                failed = true;
            }
        }
        Step(() => new StartupEntry(new RegistryStartupValues(), Environment.ProcessPath!).Remove());
        Step(Notifications.ToastService.RemoveRegistration);
        return failed ? 1 : 0;
    }
}
