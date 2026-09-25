using Microsoft.UI.Xaml;
using SoftwareUpdateTracker.Presentation;
using SoftwareUpdateTracker.Presentation.Text;

namespace SoftwareUpdateTracker.App.Pages;

// Only the Apps section so far.
public sealed partial class SettingsPage : FlyoutPage
{
    public SettingsPage()
    {
        InitializeComponent();
        TrackHeight(Top, Body, Bottom);
    }

    public string TrackedText => Words.AppsTracked(Services.Settings.Current.Apps.Count);

    public string VersionText => Words.Format(Strings.VersionLabel, Services.Version);

    private void OnBack(object sender, RoutedEventArgs e) => Back();

    private void OnChooseApps(object sender, RoutedEventArgs e) => Go(typeof(ChooseAppsPage));

    private void OnQuit(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();
}
