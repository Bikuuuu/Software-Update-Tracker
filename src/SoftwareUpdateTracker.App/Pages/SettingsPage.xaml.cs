using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;
using SoftwareUpdateTracker.Presentation;
using SoftwareUpdateTracker.Presentation.Settings;

namespace SoftwareUpdateTracker.App.Pages;

// Kept while the app runs, so it subscribes once.
public sealed partial class SettingsPage : FlyoutPage
{
    public SettingsPage()
    {
        InitializeComponent();
        TrackHeight(Top, Body, Bottom);
    }

    public SettingsViewModel Settings => Services.SettingsView;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var first = !HasServices;
        base.OnNavigatedTo(e);
        if (first) Settings.PropertyChanged += OnSettingsChanged;
    }

    protected override void Shown()
    {
        Scroller.ChangeView(null, 0, null, true);
        Settings.Open();
    }

    protected override void Hidden() => Settings.Close();

    // Narrator hears how the copy went.
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.CopyText) || Settings.CopyText is not { } text || (text != Strings.Copied && text != Strings.CopyFailed)) return;
        var peer = FrameworkElementAutomationPeer.FromElement(CopyButton) ?? FrameworkElementAutomationPeer.CreatePeerForElement(CopyButton);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, "CopyDiagnostics");
    }

    private void OnBack(object sender, RoutedEventArgs e) => Back();

    private void OnChooseApps(object sender, RoutedEventArgs e) => Go(typeof(ChooseAppsPage));

    private void OnQuit(object sender, RoutedEventArgs e) => ((App)Application.Current).Quit();

    private void OnGitHub(Hyperlink sender, HyperlinkClickEventArgs args) => Settings.OpenGitHubCommand.Execute(null);

    private void OnLicense(Hyperlink sender, HyperlinkClickEventArgs args) => Settings.OpenLicenseCommand.Execute(null);

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.DataContext is Notice notice) Settings.DismissCommand.Execute(notice);
    }
}
