using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SoftwareUpdateTracker.App.Interop;
using SoftwareUpdateTracker.App.Pages;
using SoftwareUpdateTracker.Core.Layout;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace SoftwareUpdateTracker.App;

public sealed partial class FlyoutWindow : Window
{
    private readonly nint _hwnd;
    private readonly FlyoutToggle _toggle = new(TimeProvider.System);
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private AppServices? _services;
    private FlyoutPage? _page;

    // Tray flyouts follow the Windows (taskbar) mode, not the app mode.
    private void ApplySystemTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var light = key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        Root.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
    }

    public FlyoutWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }
        Dwm.SetRoundedCorners(_hwnd);
        ApplySystemTheme();
        _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplySystemTheme);
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) Hide(); };
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            Back();
            e.Handled = true;
        };
        Root.KeyboardAccelerators.Add(escape);
        Pages.Navigated += (_, e) => Attach(e.Content as FlyoutPage);
    }

    public bool IsOpen => _toggle.IsOpen;

    // Raised when the flyout shows or hides.
    public event EventHandler? OpenChanged;

    public void Start(AppServices services)
    {
        _services = services;
        Pages.Navigate(typeof(UpdatesPage), services, new SuppressNavigationTransitionInfo());
    }

    public void Prewarm()
    {
        Dwm.SetCloaked(_hwnd, true);
        MoveToCorner();
        AppWindow.Show(false);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_toggle.IsOpen) return;
            AppWindow.Hide();
            Root.Visibility = Visibility.Collapsed;
            OpenChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void OnTrayClick()
    {
        switch (_toggle.OnTrayClick())
        {
            case ToggleAction.Open: Show(); break;
            case ToggleAction.Close: Hide(); break;
        }
    }

    // Shows the flyout, on this page when one is given.
    public void Show(Type? page = null)
    {
        if (page is not null && _services is not null && Pages.CurrentSourcePageType != page)
            Pages.Navigate(page, _services, _toggle.IsOpen ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight } : new SuppressNavigationTransitionInfo());
        if (_toggle.IsOpen) return;
        _toggle.Opened();
        Root.Visibility = Visibility.Visible;
        Root.Opacity = 0;
        Root.UpdateLayout();
        MoveToCorner();
        AppWindow.Show(true);
        // WinUI windows refuse WS_EX_TOPMOST; the flyout relies on foreground activation instead.
        NativeMethods.SetForegroundWindow(_hwnd);
        _services?.Updates.Opened();
        OpenChanged?.Invoke(this, EventArgs.Empty);
        // Uncloak one frame later so the first frame is already rendered.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            Dwm.SetCloaked(_hwnd, false);
            PlayOpenAnimation();
        });
    }

    public void Hide()
    {
        if (!_toggle.IsOpen) return;
        _toggle.Closed();
        _services?.Updates.Closed();
        Dwm.SetCloaked(_hwnd, true);
        AppWindow.Hide();
        // The next open starts on Updates. Leaving Choose apps this way still checks the apps it added.
        while (Pages.CanGoBack) Pages.GoBack(new SuppressNavigationTransitionInfo());
        // Nothing renders or animates while the flyout is hidden.
        Root.Visibility = Visibility.Collapsed;
        OpenChanged?.Invoke(this, EventArgs.Empty);
    }

    // For --self-check: loads every page out of sight and names the ones that loaded.
    // It waits for Choose apps to list apps (or to fail), and in the demo for update rows too, so the row templates are built.
    public async Task<IReadOnlyList<string>> LoadEveryPageAsync()
    {
        var loaded = new List<string>();
        if (_services is not { } services) return loaded;
        Dwm.SetCloaked(_hwnd, true);
        Root.Visibility = Visibility.Visible;
        AppWindow.Show(false);
        if (services.Demo) services.Updates.CheckNowCommand.Execute(null);
        foreach (var type in new[] { typeof(UpdatesPage), typeof(ChooseAppsPage), typeof(SettingsPage) })
        {
            if (Pages.CurrentSourcePageType != type) Pages.Navigate(type, services, new SuppressNavigationTransitionInfo());
            if (Pages.Content is not FlyoutPage page) continue;
            if (!page.IsLoaded)
            {
                var ready = new TaskCompletionSource();
                page.Loaded += (_, _) => ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (services.Demo && type == typeof(UpdatesPage)) await Until(() => services.Updates.Updates.Count > 0 && services.Updates.UpToDate.Count > 0);
            if (type == typeof(ChooseAppsPage)) await Until(() => services.Choose.Apps.Count > 0 || services.Choose.Problem is not null);
            loaded.Add(type.Name);
        }
        while (Pages.CanGoBack) Pages.GoBack(new SuppressNavigationTransitionInfo());
        AppWindow.Hide();
        Root.Visibility = Visibility.Collapsed;
        return loaded;
    }

    private static async Task Until(Func<bool> done)
    {
        for (var tries = 0; tries < 100 && !done(); tries++) await Task.Delay(100);
        // One more moment for the rows to be laid out.
        await Task.Delay(300);
    }

    // Esc goes back a page, or closes the flyout on Updates.
    private void Back()
    {
        if (Pages.CanGoBack) Pages.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
        else Hide();
    }

    private void Attach(FlyoutPage? page)
    {
        if (_page is not null) _page.NaturalHeightChanged -= OnNaturalHeightChanged;
        _page = page;
        if (page is null) return;
        page.NaturalHeightChanged += OnNaturalHeightChanged;
        Fit();
    }

    private void OnNaturalHeightChanged(object? sender, EventArgs e) => Fit();

    // Keeps the bottom edge above the taskbar while the content grows or shrinks.
    private void Fit()
    {
        if (!_toggle.IsOpen || _page is not { NaturalHeight: > 0 } page) return;
        var (work, dpi) = Screens.TaskbarMonitor();
        var rect = FlyoutPlacement.Compute(work, dpi, page.NaturalHeight);
        AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
    }

    private void MoveToCorner()
    {
        var (work, dpi) = Screens.TaskbarMonitor();
        var height = _page?.NaturalHeight ?? 0;
        if (height <= 0)
        {
            Root.Measure(new Windows.Foundation.Size(FlyoutPlacement.WidthDip, double.PositiveInfinity));
            height = Root.DesiredSize.Height;
        }
        var rect = FlyoutPlacement.Compute(work, dpi, height);
        // Move onto the target monitor first so a DPI change can't rescale the final size.
        AppWindow.Move(new PointInt32(rect.X, rect.Y));
        AppWindow.MoveAndResize(new RectInt32(rect.X, rect.Y, rect.Width, rect.Height));
    }

    private void PlayOpenAnimation()
    {
        var storyboard = new Storyboard();
        var slide = new DoubleAnimation
        {
            From = 16,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new ExponentialEase { Exponent = 7, EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, Slide);
        Storyboard.SetTargetProperty(slide, "Y");
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(150) };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
