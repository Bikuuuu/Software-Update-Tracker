using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using SoftwareUpdateTracker.Presentation.Choose;

namespace SoftwareUpdateTracker.App.Pages;

public sealed partial class ChooseAppsPage : FlyoutPage
{
    public ChooseAppsPage()
    {
        InitializeComponent();
        TrackHeight(Top, Body, Bottom);
    }

    public ChooseAppsViewModel Choose => Services.Choose;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Choose.Open();
    }

    // Done, Back, Esc or the flyout closing: rows of untracked apps leave Updates, and added apps get a check.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Services.Updates.TrackedAppsChanged(Choose.Close());
    }

    private void OnBack(object sender, RoutedEventArgs e) => Back();

    // Done returns to Updates (spec §4.4), even when Choose apps was opened from Settings.
    private void OnDone(object sender, RoutedEventArgs e) => Home();
}
