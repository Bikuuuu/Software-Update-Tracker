using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using SoftwareUpdateTracker.App.Interop;
using SoftwareUpdateTracker.Core.Layout;
using Windows.Graphics;
using Windows.Storage.Streams;
using VirtualKey = Windows.System.VirtualKey;

namespace SoftwareUpdateTracker.App;

public sealed partial class FlyoutWindow : Window
{
    private readonly nint _hwnd;
    private readonly FlyoutToggle _toggle = new(TimeProvider.System);

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
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) Hide(); };
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) => { Hide(); e.Handled = true; };
        Root.KeyboardAccelerators.Add(escape);
        _ = LoadLogoAsync();
    }

    public bool IsOpen => _toggle.IsOpen;

    public void Prewarm()
    {
        Dwm.SetCloaked(_hwnd, true);
        MoveToCorner();
        AppWindow.Show(false);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            AppWindow.Hide();
            Efficiency.EnterIdle();
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

    public void Show()
    {
        if (_toggle.IsOpen) return;
        Efficiency.ExitIdle();
        _toggle.Opened();
        Root.Opacity = 0;
        MoveToCorner();
        AppWindow.Show(true);
        // WinUI windows refuse WS_EX_TOPMOST; the flyout relies on foreground activation instead.
        NativeMethods.SetForegroundWindow(_hwnd);
        Busy.IsIndeterminate = true;
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
        Busy.IsIndeterminate = false;
        Dwm.SetCloaked(_hwnd, true);
        AppWindow.Hide();
        Efficiency.EnterIdle();
    }

    private void MoveToCorner()
    {
        var (work, dpi) = Screens.TaskbarMonitor();
        Root.Measure(new Windows.Foundation.Size(FlyoutPlacement.WidthDip, double.PositiveInfinity));
        var rect = FlyoutPlacement.Compute(work, dpi, Root.DesiredSize.Height);
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

    private async Task LoadLogoAsync()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "hamster-small.svg"));
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var svg = new SvgImageSource();
        await svg.SetSourceAsync(stream);
        Logo.Source = svg;
    }
}
