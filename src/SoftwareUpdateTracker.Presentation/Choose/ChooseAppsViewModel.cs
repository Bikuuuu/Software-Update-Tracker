using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.Inventory;
using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Core.Tracking;
using SoftwareUpdateTracker.Presentation.Settings;
using SoftwareUpdateTracker.Presentation.Text;

namespace SoftwareUpdateTracker.Presentation.Choose;

// The Choose apps page (spec §4.4). Nothing is ticked until the user ticks it; each tick is saved at once.
public sealed partial class ChooseAppsViewModel(IAppInventory inventory, SettingsStore settings, SettingsWriter writer, FileLog log, Action<Action> post) : ObservableObject
{
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private List<ChooseRow> _all = [];
    private List<ElsewhereRow> _elsewhere = [];
    private CancellationTokenSource? _loading;

    public ObservableCollection<ChooseRow> Apps { get; } = [];
    public ObservableCollection<ElsewhereRow> Elsewhere { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowSelectedOnly { get; private set; }

    [ObservableProperty]
    public partial string FilterText { get; private set; } = Strings.ShowSelected;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsElsewhereExpanded { get; private set; }

    [ObservableProperty]
    public partial bool HasElsewhere { get; private set; }

    [ObservableProperty]
    public partial string ElsewhereText { get; private set; } = "";

    [ObservableProperty]
    public partial string SelectedText { get; private set; } = "";

    // Nothing starts ticked; once something is, ticks apply as they're made.
    [ObservableProperty]
    public partial string FooterText { get; private set; } = "";

    [ObservableProperty]
    public partial bool NoMatches { get; private set; }

    [ObservableProperty]
    public partial string EmptyText { get; private set; } = Strings.NoAppsFound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial Notice? Problem { get; private set; }

    // What the banner binds to: x:Bind doesn't rerun a function once its argument is null.
    public bool HasProblem => Problem is not null;

    // Reads the installed apps: the plain list first, then again with the lookup matches moved in.
    public void Open()
    {
        _added.Clear();
        _loading?.Cancel();
        var loading = new CancellationTokenSource();
        _loading = loading;
        IsLoading = _all.Count == 0;
        Problem = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var first = new Reporter<AppInventory>(list => post(() => Fill(list, loading)));
                var result = await inventory.ReadAsync(first, loading.Token);
                post(() => Fill(result, loading));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (e is not PackageSourceException) log.Error("Reading installed apps failed", e);
                post(() => Failed(e as PackageSourceException, loading));
            }
        });
    }

    // True when apps were added, so they get a check.
    public bool Close()
    {
        _loading?.Cancel();
        _loading = null;
        var added = _added.Count > 0;
        _added.Clear();
        return added;
    }

    partial void OnSearchChanged(string value) => Filter();

    [RelayCommand]
    private void ToggleShowSelected()
    {
        ShowSelectedOnly = !ShowSelectedOnly;
        FilterText = ShowSelectedOnly ? Strings.ShowAll : Strings.ShowSelected;
        Filter();
    }

    [RelayCommand]
    private void ToggleElsewhere() => IsElsewhereExpanded = !IsElsewhereExpanded;

    [RelayCommand]
    private void Retry() => Open();

    internal void Toggled(ChooseRow row, bool tracked)
    {
        var app = row.App;
        if (tracked) _added.Add(Key(app.Id, app.Source));
        else _added.Remove(Key(app.Id, app.Source));
        writer.Update(file => tracked
            ? file.Apps.Any(a => a.Matches(app.Id, app.Source)) ? file : file with { Apps = [.. file.Apps, new TrackedApp { Id = app.Id, Source = app.Source, Name = app.Name }] }
            : file with { Apps = file.Apps.Where(a => !a.Matches(app.Id, app.Source)).ToList() },
            saved =>
            {
                if (saved) return;
                row.SetTracked(!tracked);
                _added.Remove(Key(app.Id, app.Source));
                Problem = Notice.SaveFailed;
                Counts();
            });
        Counts();
    }

    private static string Key(string id, string source) => $"{source}|{id}";

    private void Fill(AppInventory list, CancellationTokenSource loading)
    {
        if (loading != _loading) return;
        // Rows already shown keep their tick, which may not be saved yet.
        var shown = _all.ToDictionary(r => Key(r.App.Id, r.App.Source), StringComparer.OrdinalIgnoreCase);
        var tracked = settings.Current.Apps;
        _all = list.Trackable
            .Select(app => shown.TryGetValue(Key(app.Id, app.Source), out var row) ? row : new ChooseRow(this, app, tracked.Any(a => a.Matches(app.Id, app.Source))))
            .ToList();
        _elsewhere = list.Elsewhere.Select(ElsewhereRow.From).ToList();
        IsLoading = false;
        Filter();
    }

    private void Failed(PackageSourceException? error, CancellationTokenSource loading)
    {
        if (loading != _loading) return;
        IsLoading = false;
        Problem = error?.Problem is CheckProblem.WinGetMissing or CheckProblem.WinGetTooOld
            ? Notice.ForProblem(error.Problem, error.Message)
            : new Notice(NoticeKind.Check, NoticeSeverity.Error, Strings.InventoryFailed, Details: error?.Message);
    }

    private void Filter()
    {
        var search = Search.Trim();
        bool Matches(string name, string publisher) =>
            search.Length == 0 || name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        CollectionSync.Apply(Apps, _all.Where(r => (!ShowSelectedOnly || r.IsTracked) && Matches(r.App.Name, r.App.Publisher)).ToList());
        CollectionSync.Apply(Elsewhere, _elsewhere.Where(e => !ShowSelectedOnly && Matches(e.Name, e.Publisher)).ToList());
        HasElsewhere = Elsewhere.Count > 0;
        ElsewhereText = Words.Format(Strings.UpdatedElsewhere, Elsewhere.Count);
        NoMatches = !IsLoading && Apps.Count == 0 && Elsewhere.Count == 0;
        EmptyText = search.Length > 0 ? Strings.NoMatches : ShowSelectedOnly && _all.Count > 0 ? Strings.NoneSelected : Strings.NoAppsFound;
        Counts();
    }

    private void Counts()
    {
        var ticked = _all.Count(r => r.IsTracked);
        SelectedText = Words.Format(Strings.SelectedCount, ticked, _all.Count);
        FooterText = ticked == 0 ? Strings.ChooseFooter : Strings.ChooseFooterTicked;
    }

    // Reports on the calling thread, unlike Progress<T>.
    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
