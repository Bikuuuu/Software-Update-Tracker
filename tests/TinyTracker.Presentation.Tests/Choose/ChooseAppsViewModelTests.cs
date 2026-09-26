using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Inventory;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;
using TinyTracker.Presentation.Choose;
using TinyTracker.Presentation.Settings;
using Xunit;
using static TinyTracker.Presentation.Tests.Fixtures;

namespace TinyTracker.Presentation.Tests.Choose;

public sealed class ChooseAppsViewModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly InventoryApp Editor = Trackable("Example.Editor", "2.4.1");
    private static readonly InventoryApp Paint = Trackable("Example.Paint", "7.0", "Contoso");
    private static readonly InventoryApp Viewer = Trackable("Example.Viewer", "3.3");
    private static readonly ElsewhereApp Game = new("Example Game", "1.0", "Example Studio", @"ARP\Machine\X64\Steam App 123", UpdatedBy.Steam);
    private static readonly ElsewhereApp Launcher = new("Example Launcher", "Unknown", "Example", @"ARP\Machine\X64\Example Launcher", UpdatedBy.ItSelf);

    private readonly TempFolder _folder = new();
    private readonly TestUi _ui = new();
    private readonly FakeInventory _inventory = new();
    private readonly SettingsStore _settings;
    private readonly SettingsWriter _writer;
    private readonly ChooseAppsViewModel _vm;

    public ChooseAppsViewModelTests()
    {
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        var log = new FileLog(_folder.PathOf("app.log"), new FakeTimeProvider(Now));
        _writer = new SettingsWriter(_settings, log, _ui.Post);
        _vm = new ChooseAppsViewModel(_inventory, _settings, _writer, log, _ui.Post);
        _inventory.First = new AppInventory([Editor, Viewer], [Game, Launcher, new ElsewhereApp("Example Paint", "7.0", "Contoso", Paint.LocalId, UpdatedBy.Unknown)]);
    }

    public void Dispose() => _folder.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InventoryApp Trackable(string id, string version, string publisher = "Example Publisher") =>
        new(id, "winget", Name(id), version, publisher, $@"ARP\Machine\X64\{Name(id)}");

    private async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            _ui.Pump();
            if (condition()) return;
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(5, Ct);
        }
    }

    private async Task OpenFully()
    {
        _vm.Open();
        _inventory.Finish(new AppInventory([Editor, Paint, Viewer], [Game, Launcher]));
        await Until(() => _vm.Apps.Count == 3);
    }

    private async Task Saved()
    {
        await _writer.Idle.WaitAsync(Wait, Ct);
        _ui.Pump();
    }

    [Fact]
    public async Task Open_ShowsTheFirstList_ThenTheMatchesMovedIn()
    {
        _vm.Open();
        Assert.True(_vm.IsLoading);
        await Until(() => _vm.Apps.Count == 2);
        Assert.False(_vm.IsLoading);
        Assert.Equal(["Example Editor", "Example Viewer"], _vm.Apps.Select(r => r.Name));
        Assert.Equal("Updated elsewhere (3)", _vm.ElsewhereText);
        _inventory.Finish(new AppInventory([Editor, Paint, Viewer], [Game, Launcher]));
        await Until(() => _vm.Apps.Count == 3);
        Assert.Equal("Updated elsewhere (2)", _vm.ElsewhereText);
    }

    [Fact]
    public async Task NothingIsTicked_UntilTheUserChooses()
    {
        await OpenFully();
        Assert.DoesNotContain(_vm.Apps, r => r.IsTracked);
        Assert.Equal("0 of 3 selected", _vm.SelectedText);
        Assert.Equal("Nothing is ticked until you choose", _vm.FooterText);
    }

    [Fact]
    public async Task TrackedApps_AreTicked()
    {
        _settings.Update(f => f with { Apps = [App("Example.Paint")] });
        await OpenFully();
        Assert.Equal(["Example Paint"], _vm.Apps.Where(r => r.IsTracked).Select(r => r.Name));
        Assert.Equal("1 of 3 selected", _vm.SelectedText);
        Assert.Equal("Changes apply as you tick", _vm.FooterText);
    }

    [Fact]
    public async Task Ticking_TracksTheApp_AndAsksForACheck()
    {
        await OpenFully();
        _vm.Apps[0].IsTracked = true;
        await Saved();
        var app = Assert.Single(_settings.Current.Apps);
        Assert.Equal(("Example.Editor", "winget", "Example Editor"), (app.Id, app.Source, app.Name));
        Assert.True(_vm.Close());
    }

    [Fact]
    public async Task TickThenUntick_LeavesNothingToCheck()
    {
        await OpenFully();
        _vm.Apps[0].IsTracked = true;
        _vm.Apps[0].IsTracked = false;
        await Saved();
        Assert.Empty(_settings.Current.Apps);
        Assert.False(_vm.Close());
    }

    [Fact]
    public async Task Unticking_StopsTracking()
    {
        _settings.Update(f => f with { Apps = [App("Example.Paint")] });
        await OpenFully();
        _vm.Apps.Single(r => r.Name == "Example Paint").IsTracked = false;
        await Saved();
        Assert.Empty(_settings.Current.Apps);
    }

    [Fact]
    public async Task Search_FiltersBothLists_ByNameOrPublisher()
    {
        await OpenFully();
        _vm.Search = "contoso";
        Assert.Equal(["Example Paint"], _vm.Apps.Select(r => r.Name));
        Assert.Empty(_vm.Elsewhere);
        _vm.Search = " game ";
        Assert.Empty(_vm.Apps);
        Assert.Equal(["Example Game"], _vm.Elsewhere.Select(r => r.Name));
        Assert.Equal("Updated elsewhere (1)", _vm.ElsewhereText);
        _vm.Search = "nothing like this";
        Assert.True(_vm.NoMatches);
    }

    [Fact]
    public async Task ShowSelected_ListsOnlyTickedApps()
    {
        _settings.Update(f => f with { Apps = [App("Example.Viewer")] });
        await OpenFully();
        _vm.ToggleShowSelectedCommand.Execute(null);
        Assert.Equal(["Example Viewer"], _vm.Apps.Select(r => r.Name));
        Assert.Equal("Show all", _vm.FilterText);
        Assert.False(_vm.HasElsewhere);
        _vm.ToggleShowSelectedCommand.Execute(null);
        Assert.Equal(3, _vm.Apps.Count);
    }

    [Fact]
    public async Task EmptyList_SaysWhy()
    {
        await OpenFully();
        _vm.ToggleShowSelectedCommand.Execute(null);
        Assert.True(_vm.NoMatches);
        Assert.Equal("No apps selected yet", _vm.EmptyText);
        _vm.ToggleShowSelectedCommand.Execute(null);
        _vm.Search = "nothing like this";
        Assert.Equal("No apps match your search", _vm.EmptyText);
    }

    [Fact]
    public async Task NoInstalledApps_SaysNoneFound()
    {
        _inventory.First = new AppInventory([], []);
        _vm.Open();
        _inventory.Finish(new AppInventory([], []));
        await Until(() => _vm.NoMatches);
        Assert.Equal("No apps found", _vm.EmptyText);
    }

    [Fact]
    public async Task UpdatedElsewhere_StartsCollapsed_AndSaysWhatUpdatesEachApp()
    {
        await OpenFully();
        Assert.False(_vm.IsElsewhereExpanded);
        Assert.Equal(["1.0 · Updated by Steam", "Updates itself"], _vm.Elsewhere.Select(r => r.Detail));
        _vm.ToggleElsewhereCommand.Execute(null);
        Assert.True(_vm.IsElsewhereExpanded);
    }

    [Fact]
    public async Task Rows_ShowVersionAndPublisher()
    {
        await OpenFully();
        Assert.Equal("7.0 · Contoso", _vm.Apps.Single(r => r.Name == "Example Paint").Detail);
    }

    [Fact]
    public async Task WinGetFailure_ShowsTheProblem_AndRetryReadsAgain()
    {
        _inventory.Error = new PackageSourceException(CheckProblem.WinGetTooOld, "winget 1.11.510 is older than 1.29.280.");
        _vm.Open();
        await Until(() => _vm.Problem is not null);
        Assert.Equal("winget needs an update", _vm.Problem!.Title);
        Assert.False(_vm.IsLoading);
        _inventory.Error = null;
        _vm.RetryCommand.Execute(null);
        _inventory.Finish(new AppInventory([Editor], []));
        await Until(() => _vm.Apps.Count == 1);
        Assert.Null(_vm.Problem);
    }

    [Fact]
    public async Task ProblemGone_ClosesTheBanner()
    {
        _inventory.Error = new PackageSourceException(CheckProblem.WinGetTooOld, "winget 1.11.510 is older than 1.29.280.");
        _vm.Open();
        await Until(() => _vm.Problem is not null);
        Assert.True(_vm.HasProblem);
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _inventory.Error = null;
        _vm.RetryCommand.Execute(null);
        _inventory.Finish(new AppInventory([Editor], []));
        await Until(() => _vm.Apps.Count == 1);
        Assert.False(_vm.HasProblem);
        Assert.Contains(nameof(ChooseAppsViewModel.HasProblem), changed);
    }

    [Fact]
    public async Task Reopening_KeepsTheListWhileItReloads()
    {
        await OpenFully();
        _vm.Close();
        _inventory.Reset();
        _vm.Open();
        Assert.False(_vm.IsLoading);
        Assert.Equal(3, _vm.Apps.Count);
    }

    [Fact]
    public async Task EarlierRead_ThatEndsLate_IsIgnored()
    {
        var slow = _inventory.Pending;
        _vm.Open();
        await Until(() => _vm.Apps.Count == 2);
        _inventory.Reset();
        _vm.Open();
        _inventory.Finish(new AppInventory([Viewer], []));
        await Until(() => _vm.Apps.Count == 1);
        slow.SetResult(new AppInventory([Editor, Paint, Viewer], []));
        await Task.Delay(50, Ct);
        _ui.Pump();
        Assert.Equal(["Example Viewer"], _vm.Apps.Select(r => r.Name));
    }

    [Fact]
    public async Task TickThatCantBeSaved_IsUndone()
    {
        await OpenFully();
        File.Delete(_folder.PathOf("settings.json"));
        Directory.CreateDirectory(_folder.PathOf("settings.json"));
        _vm.Apps[0].IsTracked = true;
        await Saved();
        Assert.False(_vm.Apps[0].IsTracked);
        Assert.Equal(NoticeKind.SaveFailed, _vm.Problem!.Kind);
        Assert.False(_vm.Close());
    }

    // Reports First at once, then waits for the test to finish the read.
    private sealed class FakeInventory : IAppInventory
    {
        public AppInventory First { get; set; } = new([], []);
        public PackageSourceException? Error { get; set; }
        public TaskCompletionSource<AppInventory> Pending { get; private set; } = New();

        public Task<AppInventory> ReadAsync(IProgress<AppInventory>? firstList, CancellationToken ct)
        {
            if (Error is not null) return Task.FromException<AppInventory>(Error);
            firstList?.Report(First);
            return Pending.Task.WaitAsync(ct);
        }

        public void Finish(AppInventory result) => Pending.TrySetResult(result);

        public void Reset() => Pending = New();

        private static TaskCompletionSource<AppInventory> New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
