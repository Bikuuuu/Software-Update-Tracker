using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using TinyTracker.Presentation;
using TinyTracker.Presentation.Updates;

namespace TinyTracker.App.Pages;

public sealed partial class UpdatesPage : FlyoutPage
{
    private readonly Storyboard _spin = new() { RepeatBehavior = RepeatBehavior.Forever };

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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var first = !HasServices;
        base.OnNavigatedTo(e);
        if (first) Updates.PropertyChanged += OnUpdatesChanged;
        Spinning();
    }

    protected override void Shown() => Updates.Shown();

    protected override void Hidden() => Updates.Hidden();

    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdatesViewModel.IsSpinning)) Spinning();
    }

    private void Spinning()
    {
        if (Updates.IsSpinning) _spin.Begin();
        else _spin.Stop();
    }

    private void OnHistory(object sender, RoutedEventArgs e) => Go(typeof(HistoryPage));

    private void OnSettings(object sender, RoutedEventArgs e) => Go(typeof(SettingsPage));

    private void OnChooseApps(object sender, RoutedEventArgs e) => Go(typeof(ChooseAppsPage));

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (sender.DataContext is Notice notice) Updates.DismissCommand.Execute(notice);
    }
}
