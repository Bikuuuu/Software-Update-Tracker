using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace SoftwareUpdateTracker.App.Pages;

// A flyout page: a fixed top, a body that scrolls, and a fixed bottom. The flyout sizes itself to fit it.
public partial class FlyoutPage : Page
{
    private FrameworkElement[] _parts = [];
    private AppServices? _services;

    // Raised when the height the page needs without scrolling changes.
    public event EventHandler? NaturalHeightChanged;

    // The top, the whole body (not just the part in view) and the bottom, in DIPs.
    public double NaturalHeight => _parts.Sum(p => p.ActualHeight);

    protected AppServices Services => _services ?? throw new InvalidOperationException("The page was shown without being navigated to.");

    protected bool HasServices => _services is not null;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _services = (AppServices)e.Parameter;
        base.OnNavigatedTo(e);
    }

    protected void Go(Type page) => Frame.Navigate(page, Services, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    protected void Back()
    {
        if (Frame.CanGoBack) Frame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
    }

    // Straight back to Updates, the first page.
    protected void Home()
    {
        while (Frame.BackStackDepth > 1) Frame.BackStack.RemoveAt(Frame.BackStackDepth - 1);
        Back();
    }

    // The body is the panel inside the page's ScrollViewer, so its height is the full content height.
    protected void TrackHeight(FrameworkElement top, FrameworkElement body, FrameworkElement bottom)
    {
        _parts = [top, body, bottom];
        foreach (var part in _parts) part.SizeChanged += (_, _) => NaturalHeightChanged?.Invoke(this, EventArgs.Empty);
    }
}
