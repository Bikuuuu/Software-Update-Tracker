using System.Collections.ObjectModel;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoftwareUpdateTracker.Core;
using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.Launch;
using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Scheduling;
using SoftwareUpdateTracker.Core.Settings;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Presentation.Text;

namespace SoftwareUpdateTracker.Presentation.Settings;

// What the Settings page asks of Windows.
public interface IDesktop
{
    void OpenLink(string url);

    void OpenFolder(string path);

    // False when another app holds the clipboard.
    bool Copy(string text);

    // Null when winget can't answer.
    Task<string?> WinGetVersionAsync(CancellationToken ct);
}

// The Settings page (spec §4.5). Runs on the UI thread. Rows whose behavior comes in a later plan show greyed and off.
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan FeedbackShownFor = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan WinGetTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsStore _settings;
    private readonly SettingsWriter _writer;
    private readonly CheckScheduler _scheduler;
    private readonly StartupEntry _startup;
    private readonly IDesktop _desktop;
    private readonly TimeProvider _time;
    private readonly FileLog _log;
    private readonly Action<Action> _post;
    private readonly Func<(DateTimeOffset? At, CheckProblem Problem, DateTimeOffset? GoodAt)> _lastCheck;
    private readonly string _version;
    private readonly string _logs;
    private bool _quiet;
    private int _intervalRequest;
    private CancellationTokenSource? _copying;
    private ITimer? _feedback;

    public SettingsViewModel(SettingsStore settings, SettingsWriter writer, CheckScheduler scheduler, StartupEntry startup, IDesktop desktop,
        TimeProvider time, FileLog log, Action<Action> post, Func<(DateTimeOffset? At, CheckProblem Problem, DateTimeOffset? GoodAt)> lastCheck, string version, string logsFolder)
    {
        _settings = settings;
        _writer = writer;
        _scheduler = scheduler;
        _startup = startup;
        _desktop = desktop;
        _time = time;
        _log = log;
        _post = post;
        _lastCheck = lastCheck;
        _version = version;
        _logs = logsFolder;
        VersionText = Words.Format(Strings.VersionLabel, version);
        Quietly(() => IntervalIndex = IndexOf(AppSettings.CheckIntervalChoices, AppSettings.DefaultCheckIntervalHours));
    }

    public string VersionText { get; }

    // Built once, so a ComboBox never loses its selection to a new list.
    public IReadOnlyList<string> IntervalChoices { get; } = [.. AppSettings.CheckIntervalChoices.Select(Words.Hours)];

    public IReadOnlyList<string> WaitChoices { get; } = [.. AppSettings.WaitDayChoices.Select(Words.WaitDays)];

    public ObservableCollection<Notice> Notices { get; } = [];

    // Later plans turn these rows on. Until then each shows off.
    public bool SilentModeAvailable => false;

    public bool WaitDaysAvailable => false;

    public bool PauseDuringGamesAvailable => false;

    public bool SpeedLimitAvailable => false;

    public bool NotificationsAvailable => false;

    public bool ShortcutAvailable => false;

    public bool AutoSelfUpdateAvailable => false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChooseAppsName))]
    public partial string TrackedText { get; private set; } = "";

    // Narrator reads the Choose apps row as one name.
    public string ChooseAppsName => Words.Format(Strings.SpokenJoin, Strings.ChooseApps, TrackedText);

    [ObservableProperty]
    public partial int IntervalIndex { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool SilentMode { get; private set; }

    [ObservableProperty]
    public partial int WaitIndex { get; private set; }

    [ObservableProperty]
    public partial bool PauseDuringGames { get; private set; }

    [ObservableProperty]
    public partial bool SpeedLimitEnabled { get; private set; }

    [ObservableProperty]
    public partial bool ShowNotifications { get; private set; }

    [ObservableProperty]
    public partial string ShortcutText { get; private set; } = Strings.ShortcutNone;

    [ObservableProperty]
    public partial bool AutoSelfUpdate { get; private set; }

    // Nothing has been logged until there's a problem, and then the folder exists.
    [ObservableProperty]
    public partial bool CanOpenLogs { get; private set; }

    [ObservableProperty]
    public partial bool IsCopying { get; private set; }

    [ObservableProperty]
    public partial string CopyText { get; private set; } = Strings.CopyDiagnosticsHelp;

    // Each time the page shows. Values are set quietly: showing them saves nothing and writes nothing.
    public void Open()
    {
        var settings = _settings.Current.Settings;
        Quietly(() =>
        {
            IntervalIndex = IndexOf(AppSettings.CheckIntervalChoices, settings.CheckIntervalHours);
            StartWithWindows = ReadStartup();
        });
        SilentMode = SilentModeAvailable && settings.SilentMode;
        WaitIndex = WaitDaysAvailable ? IndexOf(AppSettings.WaitDayChoices, settings.AutoInstallWaitDays) : 0;
        PauseDuringGames = PauseDuringGamesAvailable && settings.PauseDuringGames;
        SpeedLimitEnabled = SpeedLimitAvailable && settings.SpeedLimitEnabled;
        ShowNotifications = NotificationsAvailable && settings.ShowNotifications;
        ShortcutText = Strings.ShortcutNone;
        AutoSelfUpdate = AutoSelfUpdateAvailable && settings.AutoSelfUpdate;
        CanOpenLogs = Directory.Exists(_logs);
        CountTracked();
        // Ticks from Choose apps may still be saving; count again once they land.
        if (!_writer.Idle.IsCompleted) _writer.Update(file => file, _ => CountTracked());
        if (_settings.Unreadable) Show(Notice.SettingsUnreadable);
        else Hide(NoticeKind.SettingsUnreadable);
    }

    // When the page hides. A copy still gathering is dropped, so the clipboard never changes behind the user's back.
    public void Close()
    {
        _copying?.Cancel();
        _copying = null;
        IsCopying = false;
        _feedback?.Dispose();
        _feedback = null;
        CopyText = Strings.CopyDiagnosticsHelp;
    }

    public void Dispose() => Close();

    partial void OnIntervalIndexChanged(int value)
    {
        if (_quiet || value < 0 || value >= AppSettings.CheckIntervalChoices.Count) return;
        var hours = AppSettings.CheckIntervalChoices[value];
        var request = ++_intervalRequest;
        _writer.Update(file => file with { Settings = file.Settings with { CheckIntervalHours = hours } }, saved =>
        {
            if (!saved) Show(Notice.SaveFailed);
            // Only the newest change's answer counts. The page then shows, and checks follow, what the file holds.
            if (request != _intervalRequest) return;
            var stored = _settings.Current.Settings.CheckIntervalHours;
            Quietly(() => IntervalIndex = IndexOf(AppSettings.CheckIntervalChoices, stored));
            var interval = TimeSpan.FromHours(stored);
            if (_scheduler.Interval != interval) _scheduler.SetInterval(interval);
        });
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_quiet) return;
        try
        {
            _startup.Set(value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            _log.Warn($"Start with Windows not changed: {e.Message}");
            Show(Notice.StartupNotChanged(e));
        }
        // Once the switch has finished its own change, it shows what Windows now holds.
        _post(() => Quietly(() => StartWithWindows = ReadStartup()));
    }

    [RelayCommand]
    private void OpenLogs()
    {
        if (Directory.Exists(_logs)) _desktop.OpenFolder(_logs);
    }

    [RelayCommand]
    private void OpenGitHub() => _desktop.OpenLink(AppInfo.RepositoryUrl);

    [RelayCommand]
    private void OpenLicense() => _desktop.OpenLink(AppInfo.LicenseUrl);

    [RelayCommand]
    private void Dismiss(Notice notice) => Notices.Remove(notice);

    // The facts the UI owns are read here; only the winget version is asked for off the UI thread.
    // The button stays enabled, so the keyboard focus stays on it; a press while copying does nothing.
    [RelayCommand]
    private void CopyDiagnostics()
    {
        if (_copying is not null) return;
        var copying = new CancellationTokenSource();
        _copying = copying;
        _feedback?.Dispose();
        _feedback = null;
        IsCopying = true;
        CopyText = Strings.Copying;
        var (at, problem, goodAt) = _lastCheck();
        var facts = DiagnosticFacts.Gather(_version, _settings.Current.Apps.Count, _settings.Current.Settings, ReadStartup(), at, problem, goodAt);
        _ = Task.Run(async () =>
        {
            string? winget = null;
            try
            {
                using var timeout = new CancellationTokenSource(WinGetTimeout, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, copying.Token);
                winget = await _desktop.WinGetVersionAsync(linked.Token);
            }
            catch (Exception e)
            {
                if (e is not OperationCanceledException) _log.Warn($"winget version not read: {e.Message}");
            }
            _post(() => Copy(copying, facts with { WinGet = winget }));
        });
    }

    private void Copy(CancellationTokenSource copying, DiagnosticFacts facts)
    {
        copying.Dispose();
        if (copying != _copying) return;
        _copying = null;
        IsCopying = false;
        CopyText = _desktop.Copy(facts.Text()) ? Strings.Copied : Strings.CopyFailed;
        ITimer? timer = null;
        timer = _time.CreateTimer(_ => _post(() =>
        {
            if (_feedback != timer) return;
            _feedback = null;
            timer?.Dispose();
            CopyText = Strings.CopyDiagnosticsHelp;
        }), null, FeedbackShownFor, Timeout.InfiniteTimeSpan);
        _feedback = timer;
    }

    private void CountTracked() => TrackedText = Words.AppsTracked(_settings.Current.Apps.Count);

    private bool ReadStartup()
    {
        try
        {
            return _startup.IsOn;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            _log.Warn($"Start with Windows not read: {e.Message}");
            return false;
        }
    }

    private void Quietly(Action set)
    {
        _quiet = true;
        try
        {
            set();
        }
        finally
        {
            _quiet = false;
        }
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

    private static int IndexOf(IReadOnlyList<int> choices, int value)
    {
        for (var i = 0; i < choices.Count; i++)
            if (choices[i] == value) return i;
        return 0;
    }
}
