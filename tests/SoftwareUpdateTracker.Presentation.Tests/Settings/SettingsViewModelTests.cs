using Microsoft.Extensions.Time.Testing;
using SoftwareUpdateTracker.Core;
using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.Launch;
using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Scheduling;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Presentation.Settings;
using Xunit;
using static SoftwareUpdateTracker.Presentation.Tests.Fixtures;

namespace SoftwareUpdateTracker.Presentation.Tests.Settings;

public sealed class SettingsViewModelTests : IAsyncDisposable
{
    private const string Exe = @"C:\Program Files\Software Update Tracker\SoftwareUpdateTracker.exe";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly TestUi _ui = new();
    private readonly FakeStartupValues _startup = new();
    private readonly FakeDesktop _desktop = new();
    private readonly CheckScheduler _scheduler;
    private readonly SettingsStore _settings;
    private readonly SettingsWriter _writer;
    private readonly SettingsViewModel _vm;

    public SettingsViewModelTests()
    {
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        _settings = new SettingsStore(SettingsPath);
        _settings.Update(f => f with { Apps = [App("Example.Editor"), App("Example.Paint")] });
        var log = new FileLog(_folder.PathOf("app.log"), _time);
        _writer = new SettingsWriter(_settings, log, _ui.Post);
        _vm = new SettingsViewModel(_settings, _writer, _scheduler, new StartupEntry(_startup, Exe), _desktop, _time, log, _ui.Post,
            () => (Now, CheckProblem.None, Now), "0.1.0", _folder.PathOf("logs"));
    }

    // Saves a test left queued land before the folder goes.
    public async ValueTask DisposeAsync()
    {
        await _writer.Idle.WaitAsync(Wait);
        _vm.Dispose();
        _scheduler.Dispose();
        _folder.Dispose();
    }

    private string SettingsPath => _folder.PathOf("settings.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task Saved()
    {
        await _writer.Idle.WaitAsync(Wait, Ct);
        _ui.Pump();
    }

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

    // Written behind the store's back, so any save would show.
    private void PlantMarker() => File.WriteAllText(SettingsPath, "{ \"marker\": true }");

    private bool MarkerKept() => File.ReadAllText(SettingsPath).Contains("marker", StringComparison.Ordinal);

    private void BlockSaves()
    {
        File.Delete(SettingsPath);
        Directory.CreateDirectory(SettingsPath);
    }

    [Fact]
    public async Task Open_ShowsTheStoredValues_AndWritesNothing()
    {
        _settings.Update(f => f with { Settings = f.Settings with { CheckIntervalHours = 12 } });
        _startup.Run = StartupCommand.Format(Exe);
        PlantMarker();
        _vm.Open();
        await Saved();
        Assert.Equal(("12 hours", true, "Choose apps, 2 apps tracked"), (_vm.IntervalChoices[_vm.IntervalIndex], _vm.StartWithWindows, _vm.ChooseAppsName));
        Assert.Equal(0, _startup.Writes);
        Assert.True(MarkerKept());
    }

    [Fact]
    public async Task OpenAfterTaskManagerTurnedTheEntryOff_ShowsOff_AndWritesNothing()
    {
        _startup.Run = StartupCommand.Format(Exe);
        _vm.Open();
        _startup.Approved = [3, 0, 0, 0];
        _vm.Open();
        await Saved();
        Assert.False(_vm.StartWithWindows);
        Assert.Equal(0, _startup.Writes);
        Assert.NotNull(_startup.Approved);
    }

    [Fact]
    public async Task IntervalChange_IsSaved_AndTimesTheChecks()
    {
        _vm.Open();
        _vm.IntervalIndex = 0;
        await Saved();
        Assert.Equal(1, _settings.Current.Settings.CheckIntervalHours);
        Assert.Equal(TimeSpan.FromHours(1), _scheduler.Interval);
        Assert.Empty(_vm.Notices);
    }

    [Fact]
    public async Task IntervalThatCantBeSaved_SnapsBack_AndKeepsTheChecksAsTheyWere()
    {
        _vm.Open();
        BlockSaves();
        _vm.IntervalIndex = 0;
        await Saved();
        Assert.Equal("6 hours", _vm.IntervalChoices[_vm.IntervalIndex]);
        Assert.Equal(TimeSpan.FromHours(6), _scheduler.Interval);
        Assert.Equal(NoticeKind.SaveFailed, Assert.Single(_vm.Notices).Kind);
    }

    [Fact]
    public async Task OlderRefusal_DoesNotUndoANewerChoiceStillBeingSaved()
    {
        _vm.Open();
        using var gate = new ManualResetEventSlim();
        using var between = new ManualResetEventSlim();
        try
        {
            BlockSaves();
            _vm.IntervalIndex = 0;
            _writer.Update(file =>
            {
                Directory.Delete(SettingsPath);
                between.Set();
                gate.Wait(Wait);
                return file;
            });
            _vm.IntervalIndex = 4;
            Assert.True(between.Wait(Wait, Ct));
            _ui.Pump();
            Assert.Equal("24 hours", _vm.IntervalChoices[_vm.IntervalIndex]);
            Assert.Equal(TimeSpan.FromHours(6), _scheduler.Interval);
            Assert.Equal(NoticeKind.SaveFailed, Assert.Single(_vm.Notices).Kind);
        }
        finally
        {
            gate.Set();
        }
        await Saved();
        Assert.Equal("24 hours", _vm.IntervalChoices[_vm.IntervalIndex]);
        Assert.Equal(24, _settings.Current.Settings.CheckIntervalHours);
        Assert.Equal(TimeSpan.FromHours(24), _scheduler.Interval);
    }

    [Fact]
    public async Task NoSelection_IsIgnored()
    {
        _vm.Open();
        PlantMarker();
        _vm.IntervalIndex = -1;
        await Saved();
        Assert.True(MarkerKept());
    }

    [Fact]
    public void StartWithWindows_TurnsTheEntryOnAndOff()
    {
        _vm.Open();
        _vm.StartWithWindows = true;
        _ui.Pump();
        Assert.Equal(StartupCommand.Format(Exe), _startup.Run);
        Assert.True(_vm.StartWithWindows);
        _vm.StartWithWindows = false;
        _ui.Pump();
        Assert.Null(_startup.Run);
        Assert.False(_vm.StartWithWindows);
    }

    [Fact]
    public void StartWithWindowsThatFails_SnapsBackAfterTheSwitch_AndExplains()
    {
        _vm.Open();
        _startup.Fail = new UnauthorizedAccessException("denied");
        _vm.StartWithWindows = true;
        Assert.True(_vm.StartWithWindows);
        _ui.Pump();
        Assert.False(_vm.StartWithWindows);
        Assert.Equal(1, _startup.Writes);
        var notice = Assert.Single(_vm.Notices);
        Assert.Equal((NoticeKind.StartupNotChanged, "Code: 0x80070005"), (notice.Kind, notice.Details));
    }

    [Fact]
    public void StartWithWindowsThatCantBeRead_ShowsOff_AndIsLogged()
    {
        _startup.ReadFail = new System.Security.SecurityException("locked by policy");
        _vm.Open();
        Assert.False(_vm.StartWithWindows);
        Assert.Contains("WARN Start with Windows not read: locked by policy", File.ReadAllText(_folder.PathOf("app.log")));
    }

    [Fact]
    public void RowsThatComeLater_ShowOff_EvenWhenStoredOn()
    {
        _settings.Update(f => f with { Settings = f.Settings with { SilentMode = true, SpeedLimitEnabled = true, AutoInstallWaitDays = 3 } });
        _vm.Open();
        Assert.Equal(
            [false, false, false, false, false, false, false],
            [_vm.SilentModeAvailable, _vm.WaitDaysAvailable, _vm.PauseDuringGamesAvailable, _vm.SpeedLimitAvailable, _vm.NotificationsAvailable, _vm.ShortcutAvailable, _vm.AutoSelfUpdateAvailable]);
        Assert.Equal((false, 0, false, false, false, "None", false), (_vm.SilentMode, _vm.WaitIndex, _vm.PauseDuringGames, _vm.SpeedLimitEnabled, _vm.ShowNotifications, _vm.ShortcutText, _vm.AutoSelfUpdate));
        Assert.True(_settings.Current.Settings.PauseDuringGames);
    }

    [Fact]
    public async Task TrackedCount_FollowsTicksStillBeingSaved()
    {
        using var gate = new ManualResetEventSlim();
        try
        {
            _writer.Update(file =>
            {
                gate.Wait(Wait);
                return file with { Apps = [.. file.Apps, App("Example.Clock")] };
            });
            _vm.Open();
            Assert.Equal("2 apps tracked", _vm.TrackedText);
        }
        finally
        {
            gate.Set();
        }
        await Saved();
        Assert.Equal("3 apps tracked", _vm.TrackedText);
    }

    [Fact]
    public void UnreadableSettings_ShowTheirNotice()
    {
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None)) _settings.Load();
        _vm.Open();
        Assert.Equal(NoticeKind.SettingsUnreadable, Assert.Single(_vm.Notices).Kind);
    }

    [Fact]
    public void LogsFolder_OpensOnlyOnceItExists()
    {
        _vm.Open();
        Assert.False(_vm.CanOpenLogs);
        Directory.CreateDirectory(_folder.PathOf("logs"));
        _vm.Open();
        Assert.True(_vm.CanOpenLogs);
        _vm.OpenLogsCommand.Execute(null);
        Assert.Equal([_folder.PathOf("logs")], _desktop.Folders);
    }

    [Fact]
    public void Links_GoToTheRepositoryAndTheLicense()
    {
        _vm.OpenGitHubCommand.Execute(null);
        _vm.OpenLicenseCommand.Execute(null);
        Assert.Equal([AppInfo.RepositoryUrl, AppInfo.LicenseUrl], _desktop.Links);
    }

    [Fact]
    public async Task Copy_PutsTheFactsOnTheClipboard_ThenTheTextComesBack()
    {
        _vm.Open();
        _vm.CopyDiagnosticsCommand.Execute(null);
        Assert.Equal("Copying…", _vm.CopyText);
        _vm.CopyDiagnosticsCommand.Execute(null);
        await Until(() => !_vm.IsCopying);
        Assert.Equal("Copied to the clipboard", _vm.CopyText);
        var text = Assert.Single(_desktop.Copied);
        Assert.Contains("Software Update Tracker 0.1.0", text);
        Assert.Contains("winget 1.29.380", text);
        Assert.Contains("Tracked apps: 2", text);
        _time.Advance(SettingsViewModel.FeedbackShownFor);
        _ui.Pump();
        Assert.Equal("For bug reports. No personal data is included.", _vm.CopyText);
    }

    [Fact]
    public async Task CopyThatFails_SaysSo()
    {
        _desktop.ClipboardBusy = true;
        _vm.CopyDiagnosticsCommand.Execute(null);
        await Until(() => !_vm.IsCopying);
        Assert.Equal("Couldn't copy. Try again.", _vm.CopyText);
    }

    [Fact]
    public async Task HidingThePageWhileCopying_LeavesTheClipboardAlone()
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _desktop.Version = answer.Task;
        _vm.CopyDiagnosticsCommand.Execute(null);
        await Until(() => _desktop.Asked > 0);
        _vm.Close();
        answer.SetResult("1.29.380");
        var deadline = DateTime.UtcNow + Wait;
        while (_ui.Pump() == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(5, Ct);
        }
        Assert.Empty(_desktop.Copied);
        Assert.Equal(("For bug reports. No personal data is included.", false), (_vm.CopyText, _vm.IsCopying));
    }

    [Fact]
    public async Task SlowWinGet_IsLeftOut_AfterFiveSeconds()
    {
        _desktop.Version = new TaskCompletionSource<string?>().Task;
        _vm.CopyDiagnosticsCommand.Execute(null);
        await Until(() => _desktop.Asked > 0);
        _time.Advance(SettingsViewModel.WinGetTimeout);
        await Until(() => !_vm.IsCopying);
        Assert.Contains("winget unavailable", Assert.Single(_desktop.Copied));
    }

    private sealed class FakeStartupValues : IStartupValues
    {
        public string? Run { get; set; }
        public byte[]? Approved { get; set; }
        public int Writes { get; private set; }
        public Exception? Fail { get; set; }
        public Exception? ReadFail { get; set; }

        public string? ReadRun() => ReadFail is null ? Run : throw ReadFail;

        public void WriteRun(string command)
        {
            Writes++;
            if (Fail is not null) throw Fail;
            Run = command;
        }

        public void DeleteRun()
        {
            Writes++;
            if (Fail is not null) throw Fail;
            Run = null;
        }

        public byte[]? ReadApproved() => Approved;

        public void DeleteApproved() => Approved = null;
    }

    private sealed class FakeDesktop : IDesktop
    {
        private int _asked;

        public List<string> Links { get; } = [];
        public List<string> Folders { get; } = [];
        public List<string> Copied { get; } = [];
        public bool ClipboardBusy { get; set; }
        public Task<string?> Version { get; set; } = Task.FromResult<string?>("1.29.380");
        public int Asked => Volatile.Read(ref _asked);

        public void OpenLink(string url) => Links.Add(url);

        public void OpenFolder(string path) => Folders.Add(path);

        public bool Copy(string text)
        {
            if (ClipboardBusy) return false;
            Copied.Add(text);
            return true;
        }

        public Task<string?> WinGetVersionAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _asked);
            return Version.WaitAsync(ct);
        }
    }
}
