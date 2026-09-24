using Microsoft.UI.Xaml;
using SoftwareUpdateTracker.App.Tray;
using SoftwareUpdateTracker.Core;

namespace SoftwareUpdateTracker.App;

public partial class App : Application
{
    private const int MenuOpen = 1;
    private const int MenuQuit = 9;
    private TrayIcon? _tray;

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico"), AppInfo.Name)
        {
            MenuItems = [(MenuOpen, "Open Software Update Tracker"), (0, "-"), (MenuQuit, "Quit")],
        };
        _tray = tray;
        tray.MenuCommand += (_, id) => { if (id == MenuQuit) Quit(); };
        tray.CloseRequested += (_, _) => Quit();
        tray.Show();

        if (SelfCheck.RequestedPath() is string path)
        {
            File.WriteAllText(path, $"{{\"trayAdded\":{(tray.Added ? "true" : "false")}}}");
            Quit();
        }
    }

    private void Quit()
    {
        _tray?.Dispose();
        Exit();
    }
}
