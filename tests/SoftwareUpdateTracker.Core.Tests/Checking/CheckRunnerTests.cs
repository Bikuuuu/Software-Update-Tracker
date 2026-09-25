using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Scheduling;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Core.Tracking;
using Xunit;

namespace SoftwareUpdateTracker.Core.Tests.Checking;

public sealed class CheckRunnerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TrackedApp Firefox = new() { Id = "Mozilla.Firefox", Source = "winget", Name = "Firefox" };
    private static readonly TrackedApp Vlc = new() { Id = "VideoLAN.VLC", Source = "winget", Name = "VLC" };

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeSource _source = new();
    private readonly FakeDates _dates = new();
    private readonly Channel<CheckCompleted> _completed = Channel.CreateUnbounded<CheckCompleted>();
    private readonly CheckScheduler _scheduler;
    private readonly SettingsStore _store;
    private readonly CheckRunner _runner;

    public CheckRunnerTests()
    {
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        _store = new SettingsStore(SettingsPath);
        _store.Update(f => f with { Apps = [Firefox] });
        _runner = new CheckRunner(_scheduler, _store, _source, _dates, _time, new FileLog(_folder.PathOf("app.log"), _time));
        _runner.Completed += (_, e) => _completed.Writer.TryWrite(e);
    }

    public void Dispose()
    {
        _runner.Dispose();
        _scheduler.Dispose();
        _folder.Dispose();
    }

    private string SettingsPath => _folder.PathOf("settings.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CatalogRead Offering(string installed, string? available) =>
        new([new PackageSnapshot("Mozilla.Firefox", "winget", "Mozilla Firefox", installed, available)], []);

    private async Task<CheckCompleted> NextCompleted() => await _completed.Reader.ReadAsync(Ct).AsTask().WaitAsync(Wait, Ct);

    private Task<CheckCompleted> StartupCheck()
    {
        _time.Advance(CheckScheduler.StartupDelay);
        return NextCompleted();
    }

    // Starts the startup check and holds it inside the source until the test releases it.
    private async Task<TaskCompletionSource<CatalogRead>> StartBlockedCheck()
    {
        var release = new TaskCompletionSource<CatalogRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Then(_ =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        _time.Advance(CheckScheduler.StartupDelay);
        await entered.Task.WaitAsync(Wait, Ct);
        return release;
    }

    [Fact]
    public async Task CheckDue_IsAnsweredWithFinished()
    {
        _source.Default = Offering("130.0", "131.0");
        var completed = await StartupCheck();
        Assert.Equal(CheckTrigger.Startup, completed.Ticket.Trigger);
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.Equal(AppStatus.Available, Assert.Single(completed.Apps).Status);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(6), _scheduler.NextCheck);
    }

    [Fact]
    public async Task Check_SavesTheMerge()
    {
        _source.Default = Offering("130.0", "131.0");
        await StartupCheck();
        var app = Assert.Single(_store.Current.Apps);
        Assert.Equal("Mozilla Firefox", app.Name);
        Assert.Equal(new Offer { Version = "131.0", FirstSeen = _time.GetUtcNow() }, app.Offer);
        var reloaded = new SettingsStore(SettingsPath);
        reloaded.Load();
        Assert.Equal(app, Assert.Single(reloaded.Current.Apps));
    }

    [Fact]
    public async Task ToggleDuringTheCheck_IsKept()
    {
        var release = await StartBlockedCheck();
        _store.Update(f => f with { Apps = [f.Apps[0] with { Auto = true }] });
        release.SetResult(Offering("130.0", "131.0"));
        await NextCompleted();
        var app = Assert.Single(_store.Current.Apps);
        Assert.True(app.Auto);
        Assert.Equal("131.0", app.Offer!.Version);
    }

    [Fact]
    public async Task AppAddedDuringTheCheck_WaitsForTheNextCheck()
    {
        var release = await StartBlockedCheck();
        _store.Update(f => f with { Apps = [.. f.Apps, Vlc] });
        release.SetResult(Offering("130.0", "131.0"));
        var completed = await NextCompleted();
        Assert.Equal(["Mozilla.Firefox"], completed.Apps.Select(c => c.App.Id));
        Assert.Equal(Vlc, _store.Current.Apps.Single(a => a.Id == Vlc.Id));
    }

    [Fact]
    public async Task AppRemovedDuringTheCheck_StaysRemoved()
    {
        var release = await StartBlockedCheck();
        _store.Update(f => f with { Apps = [] });
        release.SetResult(Offering("130.0", "131.0"));
        var completed = await NextCompleted();
        Assert.Empty(completed.Apps);
        Assert.Empty(_store.Current.Apps);
    }

    [Fact]
    public async Task NoTrackedApps_SkipsTheSource()
    {
        _store.Update(f => f with { Apps = [] });
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.Empty(completed.Apps);
        Assert.Empty(_source.Requests);
    }

    [Fact]
    public async Task SourceProblem_IsReportedAndRetried()
    {
        _source.Then(_ => throw new PackageSourceException(CheckProblem.WinGetUnreachable, "RPC server unavailable"));
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.WinGetUnreachable, completed.Problem);
        Assert.Equal("RPC server unavailable", completed.Detail);
        Assert.Equal(_time.GetUtcNow() + CheckScheduler.RetryDelays[0], _scheduler.NextCheck);
    }

    [Fact]
    public async Task SourceProblemCode_IsKeptInTheDetail()
    {
        _source.Then(_ => throw new PackageSourceException(
            CheckProblem.WinGetUnreachable, "winget stopped", new System.Runtime.InteropServices.COMException("RPC", unchecked((int)0x800706BA))));
        var completed = await StartupCheck();
        Assert.Equal("winget stopped (0x800706BA)", completed.Detail);
    }

    [Fact]
    public async Task UnexpectedError_IsLoggedAndRetried()
    {
        _source.Then(_ => Task.FromException<CatalogRead>(new InvalidOperationException("boom")));
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.Failed, completed.Problem);
        Assert.Contains("boom", File.ReadAllText(_folder.PathOf("app.log")));
        Assert.Equal(_time.GetUtcNow() + CheckScheduler.RetryDelays[0], _scheduler.NextCheck);
    }

    [Fact]
    public async Task HungCheck_EndsAtTheDeadline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Then(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new CatalogRead([], []);
        });
        _time.Advance(CheckScheduler.StartupDelay);
        await entered.Task.WaitAsync(Wait, Ct);
        _time.Advance(CheckRunner.Deadline);
        var completed = await NextCompleted();
        Assert.Equal(CheckProblem.TimedOut, completed.Problem);
        Assert.Equal("No answer within 9 minutes.", completed.Detail);
        Assert.Equal(_time.GetUtcNow() + CheckScheduler.RetryDelays[0], _scheduler.NextCheck);
    }

    [Fact]
    public async Task NewTicket_ReplacesACheckThatIgnoredItsDeadline()
    {
        var stuck = new TaskCompletionSource<CatalogRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Then(_ =>
        {
            entered.TrySetResult();
            return stuck.Task;
        });
        _source.Then(_ => Task.FromResult(Offering("130.0", "131.0")));
        _time.Advance(CheckScheduler.StartupDelay);
        await entered.Task.WaitAsync(Wait, Ct);
        // The runner's deadline passes, then the scheduler's watchdog gives up and plans a retry.
        _time.Advance(CheckScheduler.CheckTimeout);
        _time.Advance(CheckScheduler.RetryDelays[0]);
        var completed = await NextCompleted();
        Assert.Equal(CheckTrigger.Retry, completed.Ticket.Trigger);

        stuck.SetResult(Offering("130.0", "999.0"));
        await Task.Delay(200, Ct);
        Assert.False(_completed.Reader.TryRead(out _));
        Assert.Equal("131.0", Assert.Single(_store.Current.Apps).Offer!.Version);
    }

    [Fact]
    public async Task ReleaseDate_IsFetchedOnceAndKeptWithTheOffer()
    {
        _source.Default = Offering("130.0", "131.0");
        _dates.Known["Mozilla.Firefox 131.0"] = new DateOnly(2026, 9, 20);
        var first = await StartupCheck();
        Assert.Equal(new DateOnly(2026, 9, 20), Assert.Single(first.Apps).App.Offer!.ReleaseDate);
        Assert.Equal(new DateOnly(2026, 9, 20), Assert.Single(_store.Current.Apps).Offer!.ReleaseDate);

        _scheduler.CheckNow();
        await NextCompleted();
        Assert.Equal(["Mozilla.Firefox 131.0"], _dates.Asked);
    }

    [Fact]
    public async Task MissingReleaseDate_StaysUnknown()
    {
        _source.Default = Offering("130.0", "131.0");
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.Null(Assert.Single(_store.Current.Apps).Offer!.ReleaseDate);
    }

    [Fact]
    public async Task ReleaseDateError_DoesNotFailTheCheck()
    {
        _source.Default = Offering("130.0", "131.0");
        _dates.Error = new HttpRequestException("offline");
        var completed = await StartupCheck();
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.Null(Assert.Single(completed.Apps).App.Offer!.ReleaseDate);
    }

    [Fact]
    public async Task SettingsThatCantBeSaved_AreReportedAndRetried()
    {
        var release = await StartBlockedCheck();
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            release.SetResult(Offering("130.0", "131.0"));
            Assert.Equal(CheckProblem.SettingsNotSaved, (await NextCompleted()).Problem);
        }
        Assert.Equal(_time.GetUtcNow() + CheckScheduler.RetryDelays[0], _scheduler.NextCheck);
        Assert.Null(Assert.Single(_store.Current.Apps).Offer);
    }

    [Fact]
    public async Task UnreadableSettingsAtStartup_AreReadAgainOnTheNextCheck()
    {
        _source.Default = Offering("130.0", "131.0");
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _store.Load();
            Assert.Equal(CheckProblem.SettingsNotSaved, (await StartupCheck()).Problem);
        }
        _time.Advance(CheckScheduler.RetryDelays[0]);
        var retry = await NextCompleted();
        Assert.Equal(CheckProblem.None, retry.Problem);
        Assert.Equal(AppStatus.Available, Assert.Single(retry.Apps).Status);
    }

    [Fact]
    public async Task DateSaveFailure_KeepsTheMergedRows()
    {
        _source.Default = Offering("130.0", "131.0");
        FileStream? held = null;
        _dates.Reply = _ =>
        {
            // Locks settings.json between the merge's save and the date's save.
            held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return Task.FromResult<DateOnly?>(new DateOnly(2026, 9, 20));
        };
        var completed = await StartupCheck();
        held?.Dispose();
        Assert.Equal(CheckProblem.None, completed.Problem);
        var check = Assert.Single(completed.Apps);
        Assert.True(check.NewVersion);
        Assert.Null(check.App.Offer!.ReleaseDate);
        Assert.Equal("131.0", Assert.Single(_store.Current.Apps).Offer!.Version);
    }

    [Fact]
    public async Task DeadlineWhileFetchingDates_KeepsTheMergedRows()
    {
        _source.Default = Offering("130.0", "131.0");
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dates.Reply = async ct =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };
        _time.Advance(CheckScheduler.StartupDelay);
        await asked.Task.WaitAsync(Wait, Ct);
        _time.Advance(CheckRunner.Deadline);
        var completed = await NextCompleted();
        Assert.Equal(CheckProblem.None, completed.Problem);
        Assert.True(Assert.Single(completed.Apps).NewVersion);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(6), _scheduler.NextCheck);
    }

    [Fact]
    public async Task PackageGoneFromTheCatalog_IsNotInCatalog()
    {
        _source.Default = new CatalogRead([], [new PackageKey("Mozilla.Firefox", "winget")]);
        Assert.Equal(AppStatus.NotInCatalog, Assert.Single((await StartupCheck()).Apps).Status);
    }

    [Fact]
    public async Task Dispose_StopsTheRunner()
    {
        _runner.Dispose();
        _time.Advance(CheckScheduler.StartupDelay);
        await Task.Delay(100, Ct);
        Assert.Empty(_source.Requests);
        Assert.False(_completed.Reader.TryRead(out _));
    }

    // Replies to checks in order, then with Default.
    private sealed class FakeSource : IPackageSource
    {
        private readonly Queue<Func<CancellationToken, Task<CatalogRead>>> _replies = new();
        private readonly List<IReadOnlyList<TrackedApp>> _requests = [];

        public CatalogRead Default { get; set; } = new([], []);

        public IReadOnlyList<IReadOnlyList<TrackedApp>> Requests
        {
            get { lock (_replies) return [.. _requests]; }
        }

        public void Then(Func<CancellationToken, Task<CatalogRead>> reply)
        {
            lock (_replies) _replies.Enqueue(reply);
        }

        public Task<CatalogRead> ReadAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct)
        {
            Func<CancellationToken, Task<CatalogRead>>? reply;
            lock (_replies)
            {
                _requests.Add(apps);
                reply = _replies.TryDequeue(out var next) ? next : null;
            }
            return reply is null ? Task.FromResult(Default) : reply(ct);
        }
    }

    private sealed class FakeDates : IReleaseDates
    {
        private readonly List<string> _asked = [];

        public Dictionary<string, DateOnly> Known { get; } = [];
        public Exception? Error { get; set; }
        // Used instead of Known and Error when set.
        public Func<CancellationToken, Task<DateOnly?>>? Reply { get; set; }

        public IReadOnlyList<string> Asked
        {
            get { lock (_asked) return [.. _asked]; }
        }

        public Task<DateOnly?> GetAsync(string id, string version, CancellationToken ct)
        {
            lock (_asked) _asked.Add($"{id} {version}");
            if (Reply is not null) return Reply(ct);
            if (Error is not null) return Task.FromException<DateOnly?>(Error);
            return Task.FromResult<DateOnly?>(Known.TryGetValue($"{id} {version}", out var date) ? date : null);
        }
    }
}
