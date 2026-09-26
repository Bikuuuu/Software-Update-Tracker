using System.Globalization;
using System.Net.Http.Headers;
using TinyTracker.App.Interop;
using TinyTracker.Core;
using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Inventory;
using TinyTracker.Core.Launch;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.Storage;
using TinyTracker.Presentation;
using TinyTracker.Presentation.Choose;
using TinyTracker.Presentation.Demo;
using TinyTracker.Presentation.History;
using TinyTracker.Presentation.Settings;
using TinyTracker.Presentation.Updates;
using TinyTracker.WinGet;
using TinyTracker.WinGet.ReleaseDates;

namespace TinyTracker.App;

// Everything the app runs on, built once on the UI thread and disposed on Quit.
public sealed class AppServices : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly UiInbox _inbox;
    private readonly SettingsWriter _writer;
    private readonly HistoryWriter _historyWriter;

    public AppServices(Action<Action> post, Action<string> openLink, bool demo)
    {
        var time = TimeProvider.System;
        Demo = demo;
        Paths = demo ? new DataPaths(DemoFolder.Create(Path.GetTempPath())) : DataPaths.ForCurrentUser();
        FirstRun = !demo && !File.Exists(Paths.Settings);
        Log = new FileLog(Paths.Log, time);
        Settings = new SettingsStore(Paths.Settings);
        var settingsRecovered = Settings.Load();
        History = new HistoryStore(Paths.History, time);
        var historyRecovered = History.Load();
        if (demo) foreach (var entry in DemoWinGet.History(time.GetUtcNow())) History.Add(entry);
        Version = typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        // raw.githubusercontent.com asks clients to say who they are.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TinyTracker", Version));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"(+{AppInfo.RepositoryUrl})"));

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
        _writer = new SettingsWriter(Settings, Log, post);
        _historyWriter = new HistoryWriter(History, Log, post);
        Updates = new UpdatesViewModel(Scheduler, Queue, Settings, _writer, History, _historyWriter, time, post, openLink);
        Choose = new ChooseAppsViewModel(inventory, Settings, _writer, Log, post);
        // The demo never touches the registry.
        Startup = new StartupEntry(demo ? new DemoStartupValues() : new RegistryStartupValues(), Environment.ProcessPath!);
        SettingsView = new SettingsViewModel(Settings, _writer, Scheduler, Startup, new Desktop(openLink, demo, Log), time, Log, post,
            () => (Updates.LastCheckAt, Updates.LastProblem, Updates.LastGoodCheckAt), Version, Path.GetDirectoryName(Paths.Log)!);
        HistoryView = new HistoryViewModel(History, _historyWriter, Updates, time, () => CultureInfo.CurrentCulture);
        _inbox = new UiInbox(post, Log);
        History.Changed += (_, _) => _inbox.Deliver(() =>
        {
            HistoryView.Changed();
            Updates.FilesChanged();
        });
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
    public StartupEntry Startup { get; }
    public SettingsViewModel SettingsView { get; }
    public HistoryViewModel HistoryView { get; }

    // A download stops; an installer that already started finishes on its own.
    public void Dispose()
    {
        _inbox.Dispose();
        Queue.Dispose();
        Runner.Dispose();
        Scheduler.Dispose();
        Updates.Dispose();
        SettingsView.Dispose();
        _http.Dispose();
        // Changes made just before Quit still land; in the demo, a late save would bring the folder back.
        Task.WhenAll(_writer.Idle, _historyWriter.Idle, Queue.Stopped).Wait(TimeSpan.FromSeconds(2));
        if (!Demo) return;
        Log.Close();
        DemoFolder.Delete(Paths.Root);
    }
}
