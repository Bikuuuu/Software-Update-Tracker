using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using SoftwareUpdateTracker.Presentation;
using SoftwareUpdateTracker.Presentation.Updates;

namespace SoftwareUpdateTracker.App.Pages;

public sealed partial class UpdatesPage : FlyoutPage
{
    private readonly Storyboard _spin = new() { RepeatBehavior = RepeatBehavior.Forever };
    private bool _shown;

    public UpdatesPage()
    {
        InitializeComponent();
        TrackHeight(Top, Body, Bottom);
        var turn = new DoubleAnimation { From = 0, To = 360, Duration = TimeSpan.FromSeconds(1) };
        Storyboard.SetTarget(turn, Spin);
        Storyboard.SetTargetProperty(turn, "Angle");
        _spin.Children.Add(turn);
        Loaded += async (_, _) => Logo.Source = EmptyLogo.Source = await Ui.LogoAsync();
    }

    public UpdatesViewModel Updates => Services.Updates;

    public override void Shown()
    {
        _shown = true;
        Spinning();
    }

    public override void Hidden()
    {
        _shown = false;
        Spinning();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var first = !HasServices;
        base.OnNavigatedTo(e);
        if (first) Updates.PropertyChanged += OnUpdatesChanged;
    }

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdatesViewModel.IsChecking)) Spinning();
    }

    // The refresh icon turns while a check runs, and only while the flyout shows.
    private void Spinning()
    {
        if (_shown && Updates.IsChecking) _spin.Begin();
        else _spin.Stop();
    }

    private void OnSettings(object sender, RoutedEventArgs e) => Go(typeof(SettingsPage));

    private void OnChooseApps(object sender, RoutedEventArgs e) => Go(typeof(ChooseAppsPage));

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.DataContext is Notice notice) Updates.DismissCommand.Execute(notice);
    }
}
