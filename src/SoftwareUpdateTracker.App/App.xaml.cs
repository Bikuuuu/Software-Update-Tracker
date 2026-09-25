using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SoftwareUpdateTracker.App.Tray;
using SoftwareUpdateTracker.Core;
using SoftwareUpdateTracker.Core.Launch;
using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Storage;

namespace SoftwareUpdateTracker.App;

public partial class App : Application
{
    private const int MenuOpen = 1;
    private const int MenuTestToast = 5;
    private const int MenuQuit = 9;
    private readonly Notifications.ToastService _toasts = new();
    private readonly FileLog _log = new(DataPaths.ForCurrentUser().Log, TimeProvider.System);
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var flyout = new FlyoutWindow();
        _flyout = flyout;
        flyout.Prewarm();
        _toasts.ActionInvoked += (_, _) => flyout.DispatcherQueue.TryEnqueue(() => flyout.Show());
        if (_toasts.Register() is string toastError)
            _log.Error($"Toast registration failed: {toastError}");

        var tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"), AppInfo.Name)
        {
            Menu = () => [(MenuOpen, "Open Software Update Tracker", true), (MenuTestToast, "Show test notification", true), (0, "-", true), (MenuQuit, "Quit", true)],
        };
        _tray = tray;
        tray.Activated += (_, _) => flyout.OnTrayClick();
        tray.MenuCommand += (_, id) =>
        {
            if (id == MenuOpen) flyout.Show();
            else if (id == MenuTestToast) _toasts.ShowTest();
            else if (id == MenuQuit) Quit();
        };
        tray.CloseRequested += (_, _) => Quit();
        tray.Show();

        AppInstance.GetCurrent().Activated += (_, _) => flyout.DispatcherQueue.TryEnqueue(() => flyout.Show());

        if (SelfCheck.RequestedPath() is string path)
        {
            File.WriteAllText(path, $"{{\"trayAdded\":{(tray.Added ? "true" : "false")}}}");
            Quit();
            return;
        }

        if (LaunchPolicy.OpenFlyoutOnLaunch(Environment.GetCommandLineArgs().Skip(1).ToArray()))
            flyout.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => flyout.Show());
    }

    private void Quit()
    {
        _tray?.Dispose();
        _toasts.ClearHistory();
        _flyout?.Close();
        Exit();
    }
}
