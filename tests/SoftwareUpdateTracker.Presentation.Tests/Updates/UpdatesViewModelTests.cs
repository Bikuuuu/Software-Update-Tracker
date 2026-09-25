using Microsoft.Extensions.Time.Testing;
using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.History;
using SoftwareUpdateTracker.Core.Installing;
using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Scheduling;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Core.Tracking;
using SoftwareUpdateTracker.Presentation.Settings;
using SoftwareUpdateTracker.Presentation.Shell;
using SoftwareUpdateTracker.Presentation.Updates;
using Xunit;
using static SoftwareUpdateTracker.Presentation.Tests.Fixtures;

namespace SoftwareUpdateTracker.Presentation.Tests.Updates;

public sealed class UpdatesViewModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestUi _ui = new();
    private readonly FakeInstaller _installer = new();
    private readonly List<string> _opened = [];
    private readonly CheckScheduler _scheduler;
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly SettingsWriter _writer;
    private readonly UpdatesViewModel _vm;
    private int _checksDue;

    public UpdatesViewModelTests()
    {
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        _scheduler.CheckDue += (_, _) => _checksDue++;
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(f => f with { Apps = [App("Example.Editor"), App("Example.Paint"), App("Example.Viewer", offer: null)] });
        _history = new HistoryStore(_folder.PathOf("history.json"), _time);
        var log = new FileLog(_folder.PathOf("app.log"), _time);
        _writer = new SettingsWriter(_settings, log, _ui.Post);
        _vm = new UpdatesViewModel(_scheduler, _installer, _settings, _writer, _history, _time, log, _ui.Post, _opened.Add);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _scheduler.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CheckCompleted Checked(params AppCheck[] apps) => new(new CheckTicket(1, CheckTrigger.Manual), _time.GetUtcNow(), apps, CheckProblem.None);

    private CheckCompleted Failed(CheckProblem problem) => new(new CheckTicket(1, CheckTrigger.Manual), _time.GetUtcNow(), [], problem, "winget call failed (0x800706BA)");

    private void Show(params AppCheck[] apps) => _vm.CheckFinished(Checked(apps));

    private UpdateRow Row(string name) => _vm.Updates.Concat(_vm.UpToDate).Single(r => r.Name == name);

    private async Task Saved()
    {
        await _writer.Idle.WaitAsync(Wait, Ct);
        _ui.Pump();
    }

    private void Pass(TimeSpan time)
    {
        _time.Advance(time);
        _ui.Pump();
    }

    [Fact]
    public void NoTrackedApps_ShowsTheEmptyState()
    {
        _settings.Update(f => f with { Apps = [] });
        _vm.TrackedAppsChanged(added: false);
        Assert.True(_vm.IsEmpty);
        Assert.Equal("", _vm.Summary);
        Assert.Equal("Software Update Tracker: No apps chosen", _vm.Tray.Tooltip);
    }

    [Fact]
    public void BeforeTheFirstCheck_NothingIsListed()
    {
        Assert.False(_vm.IsEmpty);
        Assert.Equal("Not checked yet", _vm.Summary);
        Assert.Equal(new TrayState(TrayIconKind.Idle, "Software Update Tracker: Not checked yet"), _vm.Tray);
        _vm.CheckStarted();
        Assert.Equal(("Checking for updates…", "Checking for updates…"), (_vm.Summary, _vm.NextCheck));
        Assert.Empty(_vm.Updates);
        Assert.Empty(_vm.UpToDate);
    }

    [Fact]
    public void CheckResults_FillBothGroups_InOrder()
    {
        _settings.Update(f => f with { Apps = [.. f.Apps, App("Example.Clock"), App("Example.Legacy"), App("Example.Launcher", offer: null)] });
        Show(
            Check(AppStatus.Available),
            Check(AppStatus.UpToDate, "Example.Viewer", offer: null),
            Check(AppStatus.Skipped, "Example.Clock", skipped: "2.5.0"),
            Check(AppStatus.NotFound, "Example.Legacy", offer: null),
            Check(AppStatus.VersionUnknown, "Example.Launcher", installed: "Unknown", offer: null));
        Assert.Equal(["Example Legacy", "Example Editor"], _vm.Updates.Select(r => r.Name));
        Assert.Equal(["Example Clock", "Example Launcher", "Example Viewer"], _vm.UpToDate.Select(r => r.Name));
        Assert.Equal("3 apps are up to date", _vm.UpToDateText);
        Assert.False(_vm.IsUpToDateExpanded);
    }

    [Fact]
    public void Summary_CountsUpdates_AndSaysWhenChecked()
    {
        _vm.Opened();
        Show(Check(AppStatus.Available), Check(AppStatus.Available, "Example.Paint"));
        Assert.Equal("2 updates ready · checked just now", _vm.Summary);
        Pass(TimeSpan.FromMinutes(2));
        Assert.Equal("2 updates ready · checked 2 min ago", _vm.Summary);
    }

    [Fact]
    public void NothingToUpdate_SaysUpToDate_AndOpensTheGroup()
    {
        Show(Check(AppStatus.UpToDate, "Example.Viewer", offer: null));
        Assert.Equal("Up to date · checked just now", _vm.Summary);
        Assert.True(_vm.IsUpToDateExpanded);
        _vm.ToggleUpToDateCommand.Execute(null);
        Show(Check(AppStatus.UpToDate, "Example.Viewer", offer: null));
        Assert.False(_vm.IsUpToDateExpanded);
    }

    [Fact]
    public void Footer_ShowsTheNextCheck()
    {
        Assert.Equal("Next check in 1 min", _vm.NextCheck);
        _scheduler.SetConditions(online: false, batterySaver: false);
        Show();
        Assert.Equal("Next check is on hold", _vm.NextCheck);
    }

    [Fact]
    public void Times_RefreshOnlyWhileOpen()
    {
        Show(Check(AppStatus.Available));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, _ui.Pump());
        _vm.Opened();
        Assert.Equal("1 update ready · checked 2 min ago", _vm.Summary);
        Pass(TimeSpan.FromMinutes(1));
        Assert.Equal("1 update ready · checked 3 min ago", _vm.Summary);
        _vm.Closed();
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, _ui.Pump());
    }

    [Fact]
    public void Opening_AsksForAStaleCheck()
    {
        _vm.Opened();
        Assert.Equal(1, _checksDue);
    }

    [Fact]
    public void RefreshIcon_TurnsOnlyWhileTheFlyoutShowsACheck()
    {
        _vm.CheckStarted();
        Assert.False(_vm.IsSpinning);
        _vm.Opened();
        Assert.True(_vm.IsSpinning);
        _vm.Closed();
        Assert.False(_vm.IsSpinning);
        _vm.Opened();
        Show(Check(AppStatus.Available));
        Assert.False(_vm.IsSpinning);
    }

    [Fact]
    public void UpdateAll_QueuesAvailableAndFailedRowsOnly()
    {
        _settings.Update(f => f with { Apps = [.. f.Apps, App("Example.Notes", phantom: true), App("Example.Clock", skipped: "2.5.0")] });
        Show(
            Check(AppStatus.Available),
            Check(AppStatus.Available, "Example.Paint"),
            Check(AppStatus.UpToDate, "Example.Viewer", offer: null),
            Check(AppStatus.Phantom, "Example.Notes"),
            Check(AppStatus.Skipped, "Example.Clock", skipped: "2.5.0"));
        _vm.InstallChanged(Done(UpgradeResult.Failed, "Example.Paint", UpgradeFailure.DiskFull));
        Assert.Equal(("Update all (2)", true), (_vm.UpdateAllText, _vm.CanUpdateAll));
        _vm.UpdateAllCommand.Execute(null);
        Assert.Equal(["Example.Editor", "Example.Paint"], _installer.Enqueued.Select(r => r.Package.Id).Order());
    }

    [Fact]
    public void Update_QueuesTheOfferedVersion()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").PrimaryCommand.Execute(null);
        Assert.Equal(Request(), Assert.Single(_installer.Enqueued));
    }

    [Fact]
    public void RowAtWork_MovesToTheTop()
    {
        Show(Check(AppStatus.Available), Check(AppStatus.Available, "Example.Paint"));
        Assert.Equal(["Example Editor", "Example Paint"], _vm.Updates.Select(r => r.Name));
        _vm.InstallChanged(Item(InstallStage.Downloading, "Example.Paint", Downloading(180 * MB, 400 * MB), 20 * MB));
        Assert.Equal(["Example Paint", "Example Editor"], _vm.Updates.Select(r => r.Name));
        Assert.Equal($"{Nb("180 of 400 MB")} · {Nb("20 MB/s")}", _vm.Updates[0].View.Status);
    }

    [Fact]
    public void Updated_ShowsForAMoment_ThenJoinsUpToDate()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.UpToDate, installed: "2.5.0", offer: null)));
        Assert.Equal(RowState.Updated, Row("Example Editor").View.State);
        Assert.Contains(Row("Example Editor"), _vm.Updates);
        Pass(UpdatesViewModel.UpdatedShownFor);
        Assert.Equal(RowState.UpToDate, Row("Example Editor").View.State);
        Assert.Contains(Row("Example Editor"), _vm.UpToDate);
    }

    [Fact]
    public void Failure_StaysUntilTheOfferChanges()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.DiskFull));
        Show(Check(AppStatus.Available));
        Assert.Equal(RowState.Failed, Row("Example Editor").View.State);
        Show(Check(AppStatus.Available, offer: "2.6.0"));
        Assert.Equal((RowState.Available, "6.0"), (Row("Example Editor").View.State, Row("Example Editor").View.To.Changed));
    }

    [Fact]
    public void Cancelled_ReturnsTheRowToAvailable()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Waiting));
        Row("Example Editor").CancelCommand.Execute(null);
        Assert.Equal(new PackageKey("Example.Editor", "winget"), Assert.Single(_installer.Cancelled));
        _vm.InstallChanged(Done(UpgradeResult.Cancelled));
        Assert.Equal(RowState.Available, Row("Example Editor").View.State);
    }

    [Fact]
    public void InstallOfAnUntrackedApp_IsIgnored()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Downloading, "Example.Other"));
        Assert.Equal(["Example Editor"], _vm.Updates.Select(r => r.Name));
    }

    [Fact]
    public async Task StopTracking_ShowsUndo_ThenRemovesTheRow()
    {
        Show(Check(AppStatus.Available));
        var row = Row("Example Editor");
        row.StopTrackingCommand.Execute(null);
        Assert.True(row.IsRemoved);
        await Saved();
        Assert.DoesNotContain(_settings.Current.Apps, a => a.Id == "Example.Editor");
        Assert.Contains(row, _vm.Updates);
        Pass(UpdatesViewModel.UndoShownFor);
        Assert.DoesNotContain(row, _vm.Updates);
    }

    [Fact]
    public async Task Undo_PutsTheAppBack()
    {
        _settings.Update(f => f with { Apps = [App("Example.Editor", auto: true)] });
        Show(Check(AppStatus.Available, auto: true));
        var row = Row("Example Editor");
        row.StopTrackingCommand.Execute(null);
        await Saved();
        row.UndoCommand.Execute(null);
        await Saved();
        Pass(UpdatesViewModel.UndoShownFor);
        Assert.False(row.IsRemoved);
        Assert.Contains(row, _vm.Updates);
        Assert.True(Assert.Single(_settings.Current.Apps).Auto);
    }

    [Fact]
    public void StopTracking_CancelsAWaitingInstall()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Waiting));
        Row("Example Editor").StopTrackingCommand.Execute(null);
        Assert.Single(_installer.Cancelled);
    }

    [Fact]
    public void SkippingAFailedUpdate_MovesItToUpToDate_AndOutOfUpdateAll()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.DiskFull));
        Assert.True(_vm.CanUpdateAll);
        Row("Example Editor").SkipCommand.Execute(null);
        Assert.Equal(RowState.Skipped, Row("Example Editor").View.State);
        Assert.Contains(Row("Example Editor"), _vm.UpToDate);
        Assert.False(_vm.CanUpdateAll);
    }

    [Fact]
    public async Task Skip_MovesTheRowToUpToDate_AndUndoBringsItBack()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").SkipCommand.Execute(null);
        Assert.Equal(RowState.Skipped, Row("Example Editor").View.State);
        Assert.Contains(Row("Example Editor"), _vm.UpToDate);
        await Saved();
        Assert.Equal("2.5.0", _settings.Current.Apps.Single(a => a.Id == "Example.Editor").SkippedVersion);
        await Until(() => _history.Entries.Count == 1);
        Assert.Equal((HistoryResult.Skipped, "2.5.0"), (_history.Entries[0].Result, _history.Entries[0].ToVersion));

        Row("Example Editor").UndoSkipCommand.Execute(null);
        await Saved();
        Assert.Equal(RowState.Available, Row("Example Editor").View.State);
        Assert.Null(_settings.Current.Apps.Single(a => a.Id == "Example.Editor").SkippedVersion);
    }

    [Fact]
    public async Task ToggleAuto_ShowsThePill_AndSaves()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        Assert.True(Row("Example Editor").Auto);
        await Saved();
        Assert.True(_settings.Current.Apps.Single(a => a.Id == "Example.Editor").Auto);
    }

    [Fact]
    public async Task ChangeThatCantBeSaved_IsUndone_AndExplained()
    {
        Show(Check(AppStatus.Available));
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        Row("Example Editor").ToggleAutoCommand.Execute(null);
        await Saved();
        Assert.False(Row("Example Editor").Auto);
        Assert.Equal(NoticeKind.SaveFailed, Assert.Single(_vm.Notices).Kind);
    }

    [Fact]
    public void CheckProblem_ShowsABanner_AndKeepsTheRows()
    {
        Show(Check(AppStatus.Available));
        _vm.CheckFinished(Failed(CheckProblem.WinGetUnreachable));
        Assert.Equal(("Can't reach winget right now, retrying", "winget call failed (0x800706BA)"), (_vm.Problem!.Title, _vm.Problem.Details));
        Assert.Equal(["Example Editor"], _vm.Updates.Select(r => r.Name));
        Show(Check(AppStatus.Available));
        Assert.Null(_vm.Problem);
    }

    [Fact]
    public void GoodCheck_AfterAProblem_ClosesTheBanner()
    {
        _vm.CheckFinished(Failed(CheckProblem.WinGetTooOld));
        Assert.True(_vm.HasProblem);
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Show(Check(AppStatus.Available));
        Assert.False(_vm.HasProblem);
        Assert.Contains(nameof(UpdatesViewModel.HasProblem), changed);
    }

    [Fact]
    public void WinGetTooOld_OffersTheStore()
    {
        _vm.CheckFinished(Failed(CheckProblem.WinGetTooOld));
        Assert.Equal(("winget needs an update", true), (_vm.Problem!.Title, _vm.Problem.OffersStore));
        _vm.OpenStoreCommand.Execute(null);
        Assert.Equal(["ms-windows-store://pdp/?productid=9NBLGGH4NNS1"], _opened);
        Assert.Equal(TrayIconKind.Badge, _vm.Tray.Icon);
    }

    [Fact]
    public void WhatsNew_OpensTheReleaseNotes()
    {
        Show(Check(AppStatus.Available));
        Row("Example Editor").OpenNotesCommand.Execute(null);
        Assert.Equal(["https://example.com/notes"], _opened);
    }

    [Fact]
    public void AppsAddedDuringACheck_GetAnotherCheck()
    {
        _vm.CheckStarted();
        _vm.TrackedAppsChanged(added: true);
        Assert.Equal(0, _checksDue);
        Show(Check(AppStatus.Available));
        Assert.Equal(1, _checksDue);
    }

    [Fact]
    public void AppsAdded_AreCheckedRightAway()
    {
        _vm.TrackedAppsChanged(added: true);
        Assert.Equal(1, _checksDue);
    }

    [Fact]
    public void UntrackedApps_LeaveThePage()
    {
        Show(Check(AppStatus.Available), Check(AppStatus.UpToDate, "Example.Viewer", offer: null));
        _settings.Update(f => f with { Apps = f.Apps.Where(a => a.Id != "Example.Viewer").ToList() });
        _vm.TrackedAppsChanged(added: false);
        Assert.Empty(_vm.UpToDate);
    }

    [Fact]
    public void Tray_ShowsWork_ThenUpdatesReady_ThenUpToDate()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Item(InstallStage.Downloading, progress: Downloading(180 * MB, 400 * MB)));
        Assert.Equal(new TrayState(TrayIconKind.Working, "Software Update Tracker: Installing Example Editor (45%)"), _vm.Tray);
        _vm.InstallChanged(Done(UpgradeResult.Failed, failure: UpgradeFailure.Other));
        Assert.Equal(new TrayState(TrayIconKind.Badge, "Software Update Tracker: 1 update ready"), _vm.Tray);
        Show(Check(AppStatus.UpToDate, installed: "2.5.0", offer: null));
        Assert.Equal(new TrayState(TrayIconKind.Idle, "Software Update Tracker: Up to date"), _vm.Tray);
        _vm.CheckStarted();
        Assert.Equal(new TrayState(TrayIconKind.Working, "Software Update Tracker: Checking for updates"), _vm.Tray);
    }

    [Fact]
    public void StartupNotices_ShowDamagedAndUnreadableFiles()
    {
        Directory.CreateDirectory(_folder.PathOf("locked.json"));
        var history = new HistoryStore(_folder.PathOf("locked.json"), _time);
        history.Load();
        using var vm = new UpdatesViewModel(_scheduler, _installer, _settings, _writer, history, _time, new FileLog(_folder.PathOf("app.log"), _time), _ui.Post, _opened.Add);
        vm.ShowStartupNotices(settingsRecovered: true, historyRecovered: false);
        Assert.Equal([NoticeKind.SettingsRecovered, NoticeKind.HistoryUnreadable], vm.Notices.Select(n => n.Kind));
        vm.DismissCommand.Execute(vm.Notices[0]);
        Assert.Equal([NoticeKind.HistoryUnreadable], vm.Notices.Select(n => n.Kind));
    }

    [Fact]
    public void TimerAfterDispose_DoesNothing()
    {
        Show(Check(AppStatus.Available));
        _vm.InstallChanged(Done(UpgradeResult.Updated, after: Check(AppStatus.UpToDate, installed: "2.5.0", offer: null)));
        _vm.Dispose();
        Pass(UpdatesViewModel.UpdatedShownFor);
        Assert.Equal(RowState.Updated, Row("Example Editor").View.State);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(10, Ct);
        }
    }
}
