using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TinyTracker.Core;
using TinyTracker.Core.Checking;
using TinyTracker.Core.History;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Storage;
using TinyTracker.Core.Tracking;
using TinyTracker.Core.Versions;
using TinyTracker.Presentation.History;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Shell;
using TinyTracker.Presentation.Text;

namespace TinyTracker.Presentation.Updates;

// The Updates page. Runs on the UI thread; UpdatesWiring hands it the Core's events there.
public sealed partial class UpdatesViewModel : ObservableObject, IDisposable, IHistoryRows
{
    public static readonly TimeSpan UpdatedShownFor = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan UndoShownFor = TimeSpan.FromSeconds(5);
    // Keeps "checked 2 min ago" and "Next check in …" current while the flyout is open.
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);

    private readonly CheckScheduler _scheduler;
    private readonly IInstaller _installer;
    private readonly SettingsStore _settings;
    private readonly SettingsWriter _writer;
    private readonly HistoryStore _history;
    private readonly HistoryWriter _historyWriter;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Action<string> _openLink;
    private readonly Dictionary<string, UpdateRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _checkedAt;
    private bool _checking;
    private bool _shown;
    private bool _checkAgain;
    private bool _expandedByUser;
    private ITimer? _tick;
    private bool _disposed;

    public UpdatesViewModel(CheckScheduler scheduler, IInstaller installer, SettingsStore settings, SettingsWriter writer, HistoryStore history,
        HistoryWriter historyWriter, TimeProvider time, Action<Action> post, Action<string> openLink)
    {
        _scheduler = scheduler;
        _installer = installer;
        _settings = settings;
        _writer = writer;
        _history = history;
        _historyWriter = historyWriter;
        _time = time;
        _post = post;
        _openLink = openLink;
        Refresh();
    }

    public ObservableCollection<UpdateRow> Updates { get; } = [];
    public ObservableCollection<UpdateRow> UpToDate { get; } = [];
    public ObservableCollection<Notice> Notices { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string NextCheck { get; private set; } = "";

    [ObservableProperty]
    public partial string UpdateAllText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    public partial bool CanUpdateAll { get; private set; }

    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    // The refresh icon turns only while a check runs and the flyout shows (spec §9).
    [ObservableProperty]
    public partial bool IsSpinning { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial bool HasUpdates { get; private set; }

    [ObservableProperty]
    public partial bool HasUpToDate { get; private set; }

    [ObservableProperty]
    public partial string UpToDateText { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<UpdateRow> UpToDatePreview { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsUpToDateExpanded { get; private set; }

    // The problem of the last check, as a banner.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial Notice? Problem { get; private set; }

    // What the banner binds to: x:Bind doesn't rerun a function once its argument is null.
    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty]
    public partial TrayState Tray { get; private set; } = new(TrayIconKind.Idle, "");

    // For diagnostics: when the last check ran and what it ran into, and when the last good one ran.
    public DateTimeOffset? LastCheckAt { get; private set; }

    public CheckProblem LastProblem { get; private set; }

    public DateTimeOffset? LastGoodCheckAt => _checkedAt;

    // Raised on the UI thread each time the rows are shown anew.
    public event EventHandler? RowsChanged;

    public void Dispose()
    {
        _disposed = true;
        _tick?.Dispose();
        foreach (var row in _rows.Values) row.Timer?.Dispose();
    }

    // Shows the notices for files that were damaged or can't be read at startup.
    public void ShowStartupNotices(bool settingsRecovered, bool historyRecovered)
    {
        if (settingsRecovered) Show(Notice.SettingsRecovered);
        if (historyRecovered) Show(Notice.HistoryRecovered);
        RefreshFileNotices();
    }

    // Opening the flyout, on any page, refreshes data older than 15 minutes.
    public void FlyoutOpened() => _scheduler.FlyoutOpened();

    // The page's times and refresh icon move only while it shows.
    public void Shown()
    {
        _shown = true;
        _tick ??= _time.CreateTimer(_ => _post(Refresh), null, TickEvery, TickEvery);
        Refresh();
    }

    public void Hidden()
    {
        _shown = false;
        _tick?.Dispose();
        _tick = null;
        Refresh();
    }

    public void CheckStarted()
    {
        _checking = true;
        Refresh();
    }

    public void CheckFinished(CheckCompleted check)
    {
        _checking = false;
        LastCheckAt = check.At;
        LastProblem = check.Problem;
        Problem = Notice.ForProblem(check.Problem, check.Detail);
        if (check.Problem == CheckProblem.None) _checkedAt = check.At;
        foreach (var app in check.Apps)
        {
            var key = Key(app.App);
            if (_rows.TryGetValue(key, out var row))
            {
                row.Check = app;
                if (row.Install?.Done is not null && Outdated(row.Install, app)) row.Install = null;
            }
            else if (IsTracked(app.App))
            {
                _rows[key] = new UpdateRow(this, app, _time.GetUtcNow());
            }
        }
        RefreshFileNotices();
        if (_checkAgain)
        {
            _checkAgain = false;
            _scheduler.CheckNow();
        }
        Refresh();
    }

    public void InstallChanged(InstallItem item)
    {
        // A removed row still follows its install, so Undo shows where it is.
        if (!_rows.TryGetValue(Key(item.Request.Package), out var row)) return;
        row.Install = item;
        if (item.Done is { } done)
        {
            if (done.After is { } after) row.Check = after;
            if (done.Outcome.Result == UpgradeResult.Cancelled) row.Install = null;
            else if (done.Outcome.Result == UpgradeResult.Updated && !done.Phantom && !row.IsRemoved) After(row, UpdatedShownFor, () => row.Install = null);
        }
        Refresh();
    }

    // Leaving Choose apps: drops rows of apps no longer tracked, and checks apps that were added.
    // Ticks still being saved land first, so the check sees them.
    public void TrackedAppsChanged(bool added)
    {
        if (_writer.Idle.IsCompleted) ApplyTrackedApps(added);
        else _writer.Update(file => file, _ => ApplyTrackedApps(added));
    }

    private void ApplyTrackedApps(bool added)
    {
        if (_disposed) return;
        foreach (var key in _rows.Keys.ToList())
            if (!_rows[key].IsRemoved && !IsTracked(_rows[key].Check.App)) _rows.Remove(key);
        if (added)
        {
            if (_checking) _checkAgain = true;
            else _scheduler.CheckNow();
        }
        Refresh();
    }

    [RelayCommand]
    private void CheckNow() => _scheduler.CheckNow();

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private void UpdateAll() => Enqueue(_rows.Values.Where(r => !r.IsRemoved && r.View.CountsForUpdateAll));

    [RelayCommand]
    private void OpenStore() => _openLink(Notice.AppInstallerStoreLink);

    [RelayCommand]
    private void ToggleUpToDate()
    {
        _expandedByUser = true;
        IsUpToDateExpanded = !IsUpToDateExpanded;
    }

    [RelayCommand]
    private void Dismiss(Notice notice) => Notices.Remove(notice);

    public string LocalIdOf(PackageKey package) => _rows.TryGetValue(Key(package), out var row) ? row.LocalId : "";

    // History's Retry offers only what the row itself offers now: Update or Retry, of this very version.
    public bool CanRetry(PackageKey package, string version) => _rows.TryGetValue(Key(package), out var row) && CanRetry(row, version);

    public bool Retry(PackageKey package, string version)
    {
        if (!_rows.TryGetValue(Key(package), out var row) || !CanRetry(row, version)) return false;
        Update(row);
        return true;
    }

    private static bool CanRetry(UpdateRow row, string version) =>
        !row.IsRemoved && row.View.Action is RowAction.Update or RowAction.Retry && row.Check.Package is not null
        && row.Check.App.Offer is { } offer && PackageVersion.Same(offer.Version, version);

    internal void Primary(UpdateRow row)
    {
        if (row.View.Action == RowAction.StopTracking) StopTracking(row);
        else if (row.View.HasAction) Update(row);
    }

    internal void Update(UpdateRow row) => Enqueue([row]);

    internal void Cancel(UpdateRow row) => _installer.Cancel(row.Key);

    internal void Skip(UpdateRow row)
    {
        if (row.Check.App.Offer is not { } offer) return;
        var version = offer.Version;
        // A failure of the skipped version is no longer anything to retry or update.
        if (row.Install?.Done is not null) row.Install = null;
        var entry = new HistoryEntry
        {
            Time = _time.GetUtcNow(),
            Id = row.Key.Id,
            Source = row.Key.Source,
            Name = row.Name,
            Result = HistoryResult.Skipped,
            FromVersion = row.Check.Package?.InstalledVersion,
            ToVersion = version,
        };
        // History gets the skip once it's saved, unless an Undo or another skip came first.
        var skip = ++row.Skips;
        Change(row, app => app with { SkippedVersion = version }, check => check with { App = check.App with { SkippedVersion = version }, Status = AppStatus.Skipped }, () =>
        {
            if (row.Skips == skip) _historyWriter.Add(entry);
        });
    }

    internal void UndoSkip(UpdateRow row)
    {
        row.Skips++;
        var status = row.Check.App.Offer is { Phantom: true } ? AppStatus.Phantom : row.Check.App.Offer is null ? AppStatus.UpToDate : AppStatus.Available;
        Change(row, app => app with { SkippedVersion = null }, check => check with { App = check.App with { SkippedVersion = null }, Status = status });
    }

    internal void ToggleAuto(UpdateRow row)
    {
        var auto = !row.Check.App.Auto;
        Change(row, app => app with { Auto = auto }, check => check with { App = check.App with { Auto = auto } });
    }

    internal void OpenNotes(UpdateRow row)
    {
        if (row.Check.Package?.ReleaseNotesUrl is { } url) _openLink(url);
    }

    internal void StopTracking(UpdateRow row)
    {
        if (row.IsRemoved) return;
        if (row.View.IsActive) _installer.Cancel(row.Key);
        row.RemovedApp = _settings.Current.Apps.FirstOrDefault(a => a.Matches(row.Key.Id, row.Key.Source)) ?? row.Check.App;
        row.IsRemoved = true;
        _writer.Update(file => file with { Apps = file.Apps.Where(a => !a.Matches(row.Key.Id, row.Key.Source)).ToList() }, saved =>
        {
            if (saved) return;
            PutBack(row);
            Show(Notice.SaveFailed);
            Refresh();
        });
        After(row, UndoShownFor, () =>
        {
            if (!row.IsRemoved) return;
            _rows.Remove(Key(row.Key));
        });
        Refresh();
    }

    internal void UndoRemove(UpdateRow row)
    {
        if (!row.IsRemoved || row.RemovedApp is not { } app) return;
        PutBack(row);
        _writer.Update(file => file.Apps.Any(a => a.Matches(app.Id, app.Source)) ? file : file with { Apps = [.. file.Apps, app] }, saved =>
        {
            if (saved) return;
            Show(Notice.SaveFailed);
        });
        Refresh();
    }

    // The row may already have left the list after its 5 seconds. An update that finished meanwhile shows for its moment.
    private void PutBack(UpdateRow row)
    {
        row.Timer?.Dispose();
        row.Timer = null;
        row.IsRemoved = false;
        _rows.TryAdd(Key(row.Key), row);
        if (row.Install?.Done is { Phantom: false, Outcome.Result: UpgradeResult.Updated }) After(row, UpdatedShownFor, () => row.Install = null);
    }

    private static string Key(TrackedApp app) => Key(new PackageKey(app.Id, app.Source));

    private static string Key(PackageKey key) => $"{key.Source}|{key.Id}";

    private bool IsTracked(TrackedApp app) => _settings.Current.Apps.Any(a => a.Matches(app.Id, app.Source));

    // A finished install stops showing once the check offers something else.
    private static bool Outdated(InstallItem install, AppCheck check) =>
        check.Status is not (AppStatus.Available or AppStatus.Phantom) || check.App.Offer?.Version is not { } offered
        || !PackageVersion.Same(offered, install.Request.ToVersion);

    private void Enqueue(IEnumerable<UpdateRow> rows)
    {
        var requests = rows
            .Where(r => r.Check.Package is not null && r.Check.App.Offer is not null && !r.View.IsActive)
            .Select(r => new InstallRequest(r.Key, r.Name, r.Check.Package!.InstalledVersion, r.Check.App.Offer!.Version))
            .ToList();
        if (requests.Count > 0) _installer.Enqueue(requests);
    }

    // Shows the change at once and saves it off the UI thread; a change that can't be saved is undone.
    private void Change(UpdateRow row, Func<TrackedApp, TrackedApp> app, Func<AppCheck, AppCheck> check, Action? saved = null)
    {
        var before = row.Check;
        row.Check = check(row.Check);
        _writer.Update(file => file with { Apps = file.Apps.Select(a => a.Matches(row.Key.Id, row.Key.Source) ? app(a) : a).ToList() }, ok =>
        {
            if (ok)
            {
                saved?.Invoke();
                return;
            }
            row.Check = before;
            Show(Notice.SaveFailed);
            Refresh();
        });
        Refresh();
    }

    // Runs once on the UI thread after a delay, unless the row starts another one first.
    private void After(UpdateRow row, TimeSpan delay, Action action)
    {
        row.Timer?.Dispose();
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => _post(() =>
        {
            if (_disposed || row.Timer != timer) return;
            row.Timer = null;
            action();
            Refresh();
        }), null, delay, Timeout.InfiniteTimeSpan);
        row.Timer = timer;
    }

    private void Show(Notice notice)
    {
        if (Notices.Any(n => n.Kind == notice.Kind)) return;
        Notices.Add(notice);
    }

    private void Hide(NoticeKind kind)
    {
        if (Notices.FirstOrDefault(n => n.Kind == kind) is { } notice) Notices.Remove(notice);
    }

    // History's file changed: its notice may be gone.
    public void FilesChanged() => RefreshFileNotices();

    private void RefreshFileNotices()
    {
        if (_settings.Unreadable) Show(Notice.SettingsUnreadable);
        else Hide(NoticeKind.SettingsUnreadable);
        if (_history.Unreadable) Show(Notice.HistoryUnreadable);
        else Hide(NoticeKind.HistoryUnreadable);
    }

    private void Refresh()
    {
        if (_disposed) return;
        var now = _time.GetUtcNow();
        foreach (var row in _rows.Values) row.Show(now);
        var rows = _rows.Values.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var updates = rows.Where(r => !r.View.InUpToDateGroup || r.IsRemoved && Updates.Contains(r)).OrderBy(r => r.View.Rank).ToList();
        var upToDate = rows.Except(updates).ToList();
        CollectionSync.Apply(Updates, updates);
        CollectionSync.Apply(UpToDate, upToDate);

        var pending = rows.Count(r => !r.IsRemoved && r.View.IsPending);
        var forAll = rows.Count(r => !r.IsRemoved && r.View.CountsForUpdateAll);
        IsEmpty = _settings.Current.Apps.Count == 0 && _rows.Count == 0;
        IsChecking = _checking;
        IsSpinning = _shown && _checking;
        HasUpdates = Updates.Count > 0;
        HasUpToDate = UpToDate.Count > 0;
        UpToDateText = Words.UpToDate(UpToDate.Count);
        // Kept while its rows stay the same, so the icons don't reload during downloads.
        var preview = UpToDate.Take(4).ToList();
        if (!preview.SequenceEqual(UpToDatePreview)) UpToDatePreview = preview;
        if (!_expandedByUser) IsUpToDateExpanded = Updates.Count == 0;
        CanUpdateAll = forAll > 0;
        UpdateAllText = Words.Format(Strings.UpdateAll, forAll);
        Summary = IsEmpty ? ""
            : _checkedAt is { } checkedAt ? Words.Format(Strings.SummaryChecked, pending > 0 ? Words.UpdatesReady(pending) : Strings.AllUpToDate, Words.Ago(checkedAt, now))
            : _checking ? Strings.Checking : Strings.NotCheckedYet;
        NextCheck = _checking ? Strings.Checking
            : _scheduler.NextCheck is { } next ? Words.Format(Strings.NextCheckIn, Words.Until(next - now))
            : Strings.NextCheckOnHold;
        var installing = rows.FirstOrDefault(r => !r.IsRemoved && r.View.State is RowState.Downloading or RowState.Installing);
        IsWorking = _checking || rows.Any(r => !r.IsRemoved && r.View.IsActive);
        int? percent = installing?.View is { Indeterminate: false } view ? (int)view.Percent : null;
        Tray = TrayState.Of(IsWorking, _checking, installing?.Name, percent, Problem, pending, IsEmpty, _checkedAt is not null);
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }
}
