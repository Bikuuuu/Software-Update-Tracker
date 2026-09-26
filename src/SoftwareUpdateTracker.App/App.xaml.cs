using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SoftwareUpdateTracker.App.Interop;
using SoftwareUpdateTracker.App.Pages;
using SoftwareUpdateTracker.App.Tray;
using SoftwareUpdateTracker.Core;
using SoftwareUpdateTracker.Core.Launch;
using SoftwareUpdateTracker.Presentation;
using SoftwareUpdateTracker.Presentation.Shell;
using SoftwareUpdateTracker.Presentation.Updates;

namespace SoftwareUpdateTracker.App;

public partial class App : Application
{
    private const int MenuOpen = 1;
    private const int MenuCheckNow = 2;
    private const int MenuUpdateAll = 3;
    private const int MenuSettings = 4;
    private const int MenuQuit = 9;
    private readonly Notifications.ToastService _toasts = new();
    private AppServices? _services;
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private bool _idle;

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    // Demo mode tries the UI on made-up apps. It exists only in Debug builds.
    public static bool IsDemo(IReadOnlyList<string> args) =>
#if DEBUG
        LaunchPolicy.IsDemo(args);
#else
        false;
#endif

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var flyout = new FlyoutWindow();
        _flyout = flyout;
        var ui = flyout.DispatcherQueue;
        var services = new AppServices(action => ui.TryEnqueue(() => action()), OpenLink, IsDemo(commandLine));
        _services = services;
        flyout.Start(services);
        flyout.OpenChanged += (_, _) => UpdateIdle();
        flyout.Prewarm();

        // The demo leaves no registry entries behind.
        if (!services.Demo && _toasts.Register() is string toastError) services.Log.Error($"Toast registration failed: {toastError}");

        var tray = new TrayIcon(Asset("tray.ico"), AppInfo.Name) { Menu = Menu };
        _tray = tray;
        tray.Activated += (_, _) => flyout.OnTrayClick();
        tray.MenuCommand += (_, id) => OnMenu(id);
        tray.CloseRequested += (_, _) => Quit();
        tray.Show();
        services.Updates.PropertyChanged += OnUpdatesChanged;
        ShowTray(services.Updates.Tray);

        AppInstance.GetCurrent().Activated += (_, _) => ui.TryEnqueue(() => flyout.Show());

        if (SelfCheck.RequestedPath() is string path)
        {
            ui.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                var pages = await flyout.LoadEveryPageAsync();
                File.WriteAllText(path, $"{{\"trayAdded\":{(tray.Added ? "true" : "false")},\"pages\":[{string.Join(',', pages.Select(p => $"\"{p}\""))}]}}");
                Quit();
            });
            return;
        }

        if (LaunchPolicy.OpenFlyoutOnLaunch(commandLine))
            ui.TryEnqueue(DispatcherQueuePriority.Low, () => flyout.Show(services.FirstRun ? typeof(ChooseAppsPage) : null));
    }

    public void Quit()
    {
        _services?.Dispose();
        _tray?.Dispose();
        if (_services?.Demo != true) _toasts.ClearHistory();
        _flyout?.Close();
        Exit();
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private void OpenLink(string url)
    {
        if (Links.Openable(url) is not { } link)
        {
            _services?.Log.Warn($"Link not opened: {url}");
            return;
        }
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(link));
    }

    private IReadOnlyList<(int, string, bool)> Menu() =>
    [
        (MenuOpen, Strings.MenuOpen, true),
        (MenuCheckNow, Strings.MenuCheckNow, true),
        (MenuUpdateAll, Strings.MenuUpdateAll, _services?.Updates.CanUpdateAll == true),
        (MenuSettings, Strings.MenuSettings, true),
        (0, "-", true),
        (MenuQuit, Strings.MenuQuit, true),
    ];

    private void OnMenu(int id)
    {
        if (_services is not { } services || _flyout is not { } flyout) return;
        switch (id)
        {
            case MenuOpen:
                flyout.Show();
                break;
            case MenuCheckNow:
                services.Updates.CheckNowCommand.Execute(null);
                break;
            case MenuUpdateAll:
                services.Updates.UpdateAllCommand.Execute(null);
                break;
            case MenuSettings:
                flyout.Show(typeof(SettingsPage));
                break;
            case MenuQuit:
                Quit();
                break;
        }
    }

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_services is not { } services) return;
        if (e.PropertyName == nameof(UpdatesViewModel.Tray)) ShowTray(services.Updates.Tray);
        else if (e.PropertyName == nameof(UpdatesViewModel.IsWorking)) UpdateIdle();
    }

    private void ShowTray(TrayState state)
    {
        IReadOnlyList<string> frames = state.Icon switch
        {
            TrayIconKind.Working => [.. Enumerable.Range(0, 8).Select(frame => Asset($"tray-work-{frame}.ico"))],
            TrayIconKind.Badge => [Asset("tray-badge.ico")],
            _ => [Asset("tray.ico")],
        };
        _tray?.Update(frames, state.Tooltip);
    }

    // Efficiency mode while nothing is open or running (spec §9).
    private void UpdateIdle()
    {
        var busy = _flyout?.IsOpen == true || _services?.Updates.IsWorking == true;
        if (busy == !_idle) return;
        _idle = !busy;
        if (_idle) Efficiency.EnterIdle();
        else Efficiency.ExitIdle();
    }
}
