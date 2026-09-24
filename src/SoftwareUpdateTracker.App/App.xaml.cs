using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SoftwareUpdateTracker.App.Tray;
using SoftwareUpdateTracker.Core;

namespace SoftwareUpdateTracker.App;

public partial class App : Application
{
    private const int MenuOpen = 1;
    private const int MenuTestToast = 5;
    private const int MenuQuit = 9;
    private readonly Notifications.ToastService _toasts = new();
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
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "sut-toast-error.txt"), toastError);

        var tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"), AppInfo.Name)
        {
            MenuItems = [(MenuOpen, "Open Software Update Tracker"), (MenuTestToast, "Show test notification"), (0, "-"), (MenuQuit, "Quit")],
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
        }
    }

    private void Quit()
    {
        _tray?.Dispose();
        _flyout?.Close();
        Exit();
    }
}
