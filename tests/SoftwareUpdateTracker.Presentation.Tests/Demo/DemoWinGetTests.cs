using Microsoft.Extensions.Time.Testing;
using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.Installing;
using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Scheduling;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Presentation.Demo;
using SoftwareUpdateTracker.Presentation.History;
using SoftwareUpdateTracker.Presentation.Settings;
using SoftwareUpdateTracker.Presentation.Updates;
using Xunit;
using static SoftwareUpdateTracker.Presentation.Tests.Fixtures;

namespace SoftwareUpdateTracker.Presentation.Tests.Demo;

// The demo runs through the real check runner, install queue and page, so it shows what the app would.
public sealed class DemoWinGetTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestUi _ui = new();
    private readonly DemoWinGet _demo;
    private readonly SettingsStore _settings;
    private readonly CheckScheduler _scheduler;
    private readonly CheckRunner _runner;
    private readonly InstallQueue _queue;
    private readonly UiInbox _inbox;
    private readonly UpdatesViewModel _vm;

    public DemoWinGetTests()
    {
        _demo = new DemoWinGet(_time);
        _settings = new SettingsStore(_folder.PathOf("settings.json"));
        _settings.Update(_ => DemoWinGet.Settings(_time.GetUtcNow()));
        var history = new HistoryStore(_folder.PathOf("history.json"), _time);
        var log = new FileLog(_folder.PathOf("app.log"), _time);
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        _runner = new CheckRunner(_scheduler, _settings, _demo, _demo, _time, log);
        _queue = new InstallQueue(_demo, _demo, _settings, history, _time, log, DemoWinGet.Timings);
        _inbox = new UiInbox(_ui.Post, log);
        _vm = new UpdatesViewModel(_scheduler, _queue, _settings, new SettingsWriter(_settings, log, _ui.Post), history, new HistoryWriter(history, log, _ui.Post), _time, _ui.Post, _ => { });
        _scheduler.CheckDue += _inbox.For<CheckTicket>(_ => _vm.CheckStarted());
        _runner.Completed += _inbox.For<CheckCompleted>(_vm.CheckFinished);
        _queue.Changed += _inbox.For<InstallItem>(_vm.InstallChanged);
    }

    public void Dispose()
    {
        _inbox.Dispose();
        _vm.Dispose();
        _queue.Dispose();
        _runner.Dispose();
        _scheduler.Dispose();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IEnumerable<UpdateRow> Rows => _vm.Updates.Concat(_vm.UpToDate);

    // Moves the clock in small steps and lets the workers run, until done says so.
    private async Task Run(Func<bool> done, Action? look = null)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            _ui.Pump();
            look?.Invoke();
            if (done()) return;
            Assert.True(DateTime.UtcNow < deadline, "The demo didn't get there in time.");
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2, Ct);
        }
    }

    [Fact]
    public async Task UpdateAll_WalksThroughEveryRowState()
    {
        var seen = new HashSet<RowState>();
        var statuses = new HashSet<string>();
        void Look()
        {
            foreach (var row in Rows)
            {
                seen.Add(row.View.State);
                statuses.Add(row.View.Status);
            }
        }

        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Count() == 15, Look);
        _vm.UpdateAllCommand.Execute(null);
        await Run(() => seen.Contains(RowState.Waiting) && !_vm.IsWorking && Rows.All(r => r.View.State != RowState.Updated), Look);

        Assert.Equal(Enum.GetValues<RowState>().Order(), seen.Order());
        Assert.Contains("Waiting for another install to finish…", statuses);
        Assert.Contains("Permission was declined", statuses);
        Assert.Contains("Download stalled", statuses);
    }

    [Fact]
    public async Task SecondAndThirdChecks_ShowTheBanners()
    {
        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Any());
        _vm.CheckNowCommand.Execute(null);
        await Run(() => _vm.Problem is not null);
        Assert.Equal("Can't reach winget right now, retrying", _vm.Problem!.Title);
        _vm.CheckNowCommand.Execute(null);
        await Run(() => _vm.Problem?.OffersStore == true);
        _vm.CheckNowCommand.Execute(null);
        await Run(() => _vm.Problem is null && !_vm.IsChecking);
        Assert.Equal(15, Rows.Count());
    }

    [Fact]
    public async Task Inventory_MovesTheMatchedAppIn()
    {
        var first = new List<string>();
        var task = _demo.ReadAsync(new Reported(list => first.AddRange(list.Trackable.Select(a => a.Name))), Ct);
        await Run(() => task.IsCompleted);
        var final = await task;
        Assert.DoesNotContain("Northwind Budget", first);
        Assert.Contains(final.Trackable, a => a.Name == "Northwind Budget");
        Assert.DoesNotContain(final.Elsewhere, a => a.Name == "Northwind Budget");
        Assert.Contains(final.Elsewhere, a => a.UpdatedBy == Core.Inventory.UpdatedBy.Steam);
    }

    [Fact]
    public async Task History_ShowsEveryKindOfEntry_AndRetriesOnlyTheOneThatFits()
    {
        var history = new HistoryStore(_folder.PathOf("demo-history.json"), _time);
        foreach (var entry in DemoWinGet.History(_time.GetUtcNow())) history.Add(entry);
        var log = new FileLog(_folder.PathOf("demo.log"), _time);
        var page = new HistoryViewModel(history, new HistoryWriter(history, log, _ui.Post), _vm, _time, () => System.Globalization.CultureInfo.InvariantCulture);
        _time.Advance(CheckScheduler.StartupDelay);
        await Run(() => Rows.Count() == 15);
        page.Shown();
        var rows = page.Groups.SelectMany(g => g.Rows).ToList();
        Assert.Equal(Enum.GetValues<HistoryIcon>().Order(), rows.Select(r => r.Icon).Distinct().Order());
        Assert.Equal(["Fabrikam Chat", "Proseware Maps"], rows.Where(r => r.CanRetry).Select(r => r.Name));
        Assert.Contains(rows, r => r.IsFailed && !r.HasDetails);
        Assert.Equal(["Today", "Yesterday", "Sep 23", "Sep 22"], page.Groups.Select(g => g.Title));
    }

    private sealed class Reported(Action<Core.Inventory.AppInventory> report) : IProgress<Core.Inventory.AppInventory>
    {
        public void Report(Core.Inventory.AppInventory value) => report(value);
    }
}
