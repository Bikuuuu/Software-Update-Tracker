using System.Net.Http.Headers;
using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.Installing;
using SoftwareUpdateTracker.Core.Inventory;
using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Scheduling;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Presentation;
using SoftwareUpdateTracker.Presentation.Choose;
using SoftwareUpdateTracker.Presentation.Demo;
using SoftwareUpdateTracker.Presentation.Settings;
using SoftwareUpdateTracker.Presentation.Updates;
using SoftwareUpdateTracker.WinGet;
using SoftwareUpdateTracker.WinGet.ReleaseDates;

namespace SoftwareUpdateTracker.App;

// Everything the app runs on, built once on the UI thread and disposed on Quit.
public sealed class AppServices : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly UiInbox _inbox;

    public AppServices(Action<Action> post, Action<string> openLink, bool demo)
    {
        var time = TimeProvider.System;
        Demo = demo;
        Paths = demo ? DemoPaths() : DataPaths.ForCurrentUser();
        FirstRun = !demo && !File.Exists(Paths.Settings);
        Log = new FileLog(Paths.Log, time);
        Settings = new SettingsStore(Paths.Settings);
        var settingsRecovered = Settings.Load();
        History = new HistoryStore(Paths.History, time);
        var historyRecovered = History.Load();
        Version = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        // raw.githubusercontent.com asks clients to say who they are.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SoftwareUpdateTracker", Version));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/Bikuuuu/Software-Update-Tracker)"));

        IPackageSource source;
        IPackageUpgrader upgrader;
        IAppInventory inventory;
        IReleaseDates dates;
        InstallTimings? timings = null;
        if (demo)
        {
            var fake = new DemoWinGet(time);
            (source, upgrader, inventory, dates, timings) = (fake, fake, fake, fake, DemoWinGet.Timings);
            Settings.Update(_ => DemoWinGet.Settings(time.GetUtcNow()));
        }
        else
        {
            source = new WinGetPackageSource(async ct => await WinGetSession.OpenAsync(ct));
            inventory = new WinGetInventory(async ct => await WinGetSession.OpenAsync(ct));
            upgrader = new WinGetUpgrader();
            dates = new GitHubReleaseDates(_http, time);
        }

        Scheduler = new CheckScheduler(time, TimeSpan.FromHours(Settings.Current.Settings.CheckIntervalHours));
        Runner = new CheckRunner(Scheduler, Settings, source, dates, time, Log);
        Queue = new InstallQueue(upgrader, source, Settings, History, time, Log, timings);
        var writer = new SettingsWriter(Settings, Log, post);
        Updates = new UpdatesViewModel(Scheduler, Queue, Settings, writer, History, time, Log, post, openLink);
        Choose = new ChooseAppsViewModel(inventory, Settings, writer, Log, post);
        _inbox = new UiInbox(post, Log);
        Scheduler.CheckDue += _inbox.For<CheckTicket>(_ => Updates.CheckStarted());
        Runner.Completed += _inbox.For<CheckCompleted>(Updates.CheckFinished);
        Queue.Changed += _inbox.For<InstallItem>(Updates.InstallChanged);
        Updates.ShowStartupNotices(settingsRecovered, historyRecovered);
    }

    public bool Demo { get; }
    // No settings file yet: the flyout opens on Choose apps.
    public bool FirstRun { get; }
    public string Version { get; }
    public DataPaths Paths { get; }
    public FileLog Log { get; }
    public SettingsStore Settings { get; }
    public HistoryStore History { get; }
    public CheckScheduler Scheduler { get; }
    public CheckRunner Runner { get; }
    public InstallQueue Queue { get; }
    public UpdatesViewModel Updates { get; }
    public ChooseAppsViewModel Choose { get; }

    // A download stops; an installer that already started finishes on its own.
    public void Dispose()
    {
        _inbox.Dispose();
        Queue.Dispose();
        Runner.Dispose();
        Scheduler.Dispose();
        Updates.Dispose();
        _http.Dispose();
        if (Demo) TryDelete(Paths.Root);
    }

    // A fresh folder per demo run, deleted on Quit. Folders left by a demo that crashed go at the next start.
    private static DataPaths DemoPaths()
    {
        foreach (var old in Directory.EnumerateDirectories(Path.GetTempPath(), "sut-demo-*")) TryDelete(old);
        return new DataPaths(Path.Combine(Path.GetTempPath(), "sut-demo-" + Guid.NewGuid().ToString("N")));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
