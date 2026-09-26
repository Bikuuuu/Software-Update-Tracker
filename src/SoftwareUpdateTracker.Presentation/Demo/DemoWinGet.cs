using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.History;
using SoftwareUpdateTracker.Core.Installing;
using SoftwareUpdateTracker.Core.Inventory;
using SoftwareUpdateTracker.Core.Storage;
using SoftwareUpdateTracker.Core.Tracking;

namespace SoftwareUpdateTracker.Presentation.Demo;

// Made-up apps whose updates walk through every row state, so the UI can be tried without touching real apps.
// The app uses it only in Debug builds, when started with --demo. The second and third checks fail, to show the banners.
public sealed class DemoWinGet(TimeProvider time) : IPackageSource, IPackageUpgrader, IAppInventory, IReleaseDates
{
    public const string Source = "winget";

    // Short enough to watch: a stalled download is retried after 6 seconds, a busy one after 5.
    public static InstallTimings Timings { get; } = new(TimeSpan.FromSeconds(5), 3, TimeSpan.FromSeconds(6), TimeSpan.FromMinutes(2));

    private const ulong MB = 1024 * 1024;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, App> _apps = Apps().ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    private int _checks;
    private int _busyAnswers;

    private enum Script
    {
        Updates,
        UpdatesWithoutSize,
        DiskFull,
        InUse,
        Restart,
        Phantom,
        BusyTwice,
        Stalls,
        NeedsAdmin,
        Declined,
    }

    // The tracked apps the demo starts with: one skipped version and one app on Auto.
    public static SettingsFile Settings(DateTimeOffset now) => new()
    {
        Apps = [.. Apps().Where(a => a.Tracked).Select(a => new TrackedApp
        {
            Id = a.Id,
            Source = Source,
            Name = a.Name,
            Auto = a.Id == "Tailspin.Player",
            SkippedVersion = a.Id == "Northwind.Clock" ? a.Available : null,
            Offer = a.Available is null ? null : new Offer { Version = a.Available, FirstSeen = now - TimeSpan.FromDays(2) },
        })],
    };

    // History the demo starts with: every kind of entry over four days. Fabrikam Chat's failure can be retried once a check
    // has run; Wingtip Studio's can't, because a later update followed it.
    public static IReadOnlyList<HistoryEntry> History(DateTimeOffset now)
    {
        HistoryEntry Entry(TimeSpan age, string id, string name, HistoryResult result, string? from, string to, string? reason = null, string? code = null) => new()
        {
            Time = now - age,
            Id = id,
            Source = Source,
            Name = name,
            Result = result,
            FromVersion = from,
            ToVersion = to,
            Reason = reason,
            Code = code,
        };
        return
        [
            Entry(TimeSpan.FromHours(1), "Fabrikam.Chat", "Fabrikam Chat", HistoryResult.Failed, "1.9.3", "1.10.0", "DiskFull", "0x8A150105"),
            Entry(TimeSpan.FromHours(2), "Proseware.Maps", "Proseware Maps", HistoryResult.Failed, "2025.1", "2025.2", "NeedsAdmin", "0x8A150019"),
            Entry(TimeSpan.FromDays(1), "Contoso.Editor", "Contoso Editor", HistoryResult.Updated, "2.4.0", "2.4.1"),
            Entry(TimeSpan.FromDays(1.1), "Tailspin.Player", "Tailspin Player", HistoryResult.Updated, "3.0.19", "3.0.20", "RestartNeeded"),
            Entry(TimeSpan.FromDays(2), "Litware.Sync", "Litware Sync", HistoryResult.Updated, "5.0.9", "5.1.0", "Phantom"),
            Entry(TimeSpan.FromDays(2.1), "Northwind.Clock", "Northwind Clock", HistoryResult.Skipped, "1.0", "1.1"),
            Entry(TimeSpan.FromDays(2.2), "Wingtip.Studio", "Wingtip Studio", HistoryResult.Updated, "7.9", "8.0"),
            Entry(TimeSpan.FromDays(3), "Wingtip.Studio", "Wingtip Studio", HistoryResult.Failed, "7.9", "8.0", "Other"),
            Entry(TimeSpan.FromDays(3.1), "Adatum.Photos", "Adatum Photos", HistoryResult.Cancelled, "11.9", "12.0"),
        ];
    }

    public async Task<CatalogRead> ReadAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.5), time, ct);
        // A read of one app comes from the install queue, not a check.
        var check = apps.Count > 1 ? Interlocked.Increment(ref _checks) : 0;
        if (check == 2) throw new PackageSourceException(CheckProblem.WinGetUnreachable, "Demo: winget didn't answer.");
        if (check == 3) throw new PackageSourceException(CheckProblem.WinGetTooOld, "Demo: winget 1.11.510 is older than 1.29.280.");
        lock (_gate)
        {
            var installed = new List<PackageSnapshot>();
            var gone = new List<PackageKey>();
            foreach (var tracked in apps)
            {
                if (!_apps.TryGetValue(tracked.Id, out var app) || !app.InCatalog) gone.Add(new PackageKey(tracked.Id, tracked.Source));
                else if (app.Installed is { } version) installed.Add(new PackageSnapshot(app.Id, Source, app.Name, version, app.Available, app.Publisher, app.Notes));
            }
            return new CatalogRead(installed, gone);
        }
    }

    public async Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        App app;
        lock (_gate) app = _apps[package.Id];
        try
        {
            switch (app.Script)
            {
                case Script.BusyTwice when Interlocked.Increment(ref _busyAnswers) <= 2:
                    await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
                    return new UpgradeOutcome(UpgradeResult.Busy, Code: "0x8A150102");
                case Script.NeedsAdmin:
                    await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
                    return new UpgradeOutcome(UpgradeResult.NeedsAdmin, Code: "0x8A150019");
                case Script.Declined:
                    await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
                    return new UpgradeOutcome(UpgradeResult.PermissionDeclined, Code: "0x800704C7");
            }
            await DownloadAsync(app, progress, ct);
            if (app.Script == Script.DiskFull) return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.DiskFull, "0x8A150105");
        }
        catch (OperationCanceledException)
        {
            return new UpgradeOutcome(UpgradeResult.Cancelled);
        }
        // Like winget, a started installer can't be cancelled.
        await InstallAsync(app, progress);
        switch (app.Script)
        {
            case Script.InUse:
                return new UpgradeOutcome(UpgradeResult.AppInUse, Code: "0x8A150101");
            case Script.Restart:
                return new UpgradeOutcome(UpgradeResult.RestartNeeded, Code: "installer 3010");
            case Script.Phantom:
                return new UpgradeOutcome(UpgradeResult.Updated);
        }
        lock (_gate)
        {
            app.Installed = version;
            app.Available = null;
        }
        return new UpgradeOutcome(UpgradeResult.Updated);
    }

    public async Task<AppInventory> ReadAsync(IProgress<AppInventory>? firstList, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
        lock (_gate) firstList?.Report(Inventory(matched: false));
        await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
        lock (_gate) return Inventory(matched: true);
    }

    public Task<DateOnly?> GetAsync(string id, string version, CancellationToken ct) =>
        Task.FromResult<DateOnly?>(id == "Contoso.Editor" ? DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-5) : null);

    private async Task DownloadAsync(App app, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        const ulong total = 120 * MB;
        var known = app.Script != Script.UpdatesWithoutSize;
        progress?.Report(new UpgradeProgress(UpgradeStage.Queued, 0, 0, 0, 0));
        for (ulong done = 0; done < total; done += 8 * MB)
        {
            progress?.Report(new UpgradeProgress(UpgradeStage.Downloading, done, known ? total : 0, known ? (double)done / total : 0, 0));
            // A stalled download stops moving until the queue gives up on it.
            if (app.Script == Script.Stalls && done >= 24 * MB) await Task.Delay(Timeout.InfiniteTimeSpan, time, ct);
            await Task.Delay(Step, time, ct);
        }
    }

    private async Task InstallAsync(App app, IProgress<UpgradeProgress>? progress)
    {
        for (var step = 0; step <= 8; step++)
        {
            var fraction = app.Script == Script.UpdatesWithoutSize ? 0 : step / 8.0;
            progress?.Report(new UpgradeProgress(UpgradeStage.Installing, 120 * MB, 120 * MB, 1, fraction));
            await Task.Delay(Step, time, CancellationToken.None);
        }
    }

    // The plain list misses one app; the matching pass then moves it into the tickable list.
    private AppInventory Inventory(bool matched)
    {
        var trackable = _apps.Values
            .Where(a => a.Installed is { } version && version != "Unknown" && a.InCatalog && (matched || a.Id != "Northwind.Budget"))
            .Select(a => new InventoryApp(a.Id, Source, a.Name, a.Installed!, a.Publisher, ""))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<ElsewhereApp> elsewhere =
        [
            new("Contoso Launcher", "Unknown", "Contoso", "", UpdatedBy.ItSelf),
            new("Example Game", "1.0", "Example Studio", "", UpdatedBy.Steam),
            new("Fabrikam Audio Driver", "6.0.1", "Fabrikam", "", UpdatedBy.DriverTool),
            new("Litware Store App", "2.3.0", "Litware", "", UpdatedBy.MicrosoftStore),
            new("Windows Example Runtime", "10.0.1", "Microsoft Corporation", "", UpdatedBy.WindowsUpdate),
            new("Woodgrove Toolkit", "4.1", "Woodgrove", "", UpdatedBy.Unknown),
        ];
        if (!matched) elsewhere.Add(new ElsewhereApp("Northwind Budget", "3.2", "Northwind", "", UpdatedBy.Unknown));
        return new AppInventory(trackable, [.. elsewhere.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static IEnumerable<App> Apps() =>
    [
        new("Contoso.Editor", "Contoso Editor", "Contoso", "2.4.1", "2.5.0", Script.Updates),
        new("Fabrikam.Chat", "Fabrikam Chat", "Fabrikam", "1.9.3", "1.10.0", Script.DiskFull),
        new("Northwind.Notes", "Northwind Notes", "Northwind", "7.2", "7.3", Script.InUse),
        new("Tailspin.Player", "Tailspin Player", "Tailspin", "3.0.20", "3.0.21", Script.Restart),
        new("Litware.Sync", "Litware Sync", "Litware", "5.1.0", "5.1.2", Script.Phantom),
        new("Adatum.Photos", "Adatum Photos", "Adatum", "12.0", "12.1", Script.BusyTwice),
        new("Woodgrove.Wallet", "Woodgrove Wallet", "Woodgrove", "4.4.0", "4.5.0", Script.Stalls),
        new("Proseware.Maps", "Proseware Maps", "Proseware", "2025.1", "2025.2", Script.NeedsAdmin),
        new("Wingtip.Studio", "Wingtip Studio", "Wingtip", "8.0", "8.1", Script.UpdatesWithoutSize),
        new("Litware.Reader", "Litware Reader", "Litware", "6.2.0", "6.3.0", Script.Declined),
        new("Fabrikam.Viewer", "Fabrikam Viewer", "Fabrikam", "3.3", null, Script.Updates),
        new("Northwind.Clock", "Northwind Clock", "Northwind", "1.0", "1.1", Script.Updates),
        new("Contoso.Launcher", "Contoso Launcher", "Contoso", "Unknown", null, Script.Updates),
        new("Adatum.Legacy", "Adatum Legacy", "Adatum", null, null, Script.Updates),
        new("Tailspin.Tools", "Tailspin Tools", "Tailspin", "1.0", null, Script.Updates) { InCatalog = false },
        new("Northwind.Budget", "Northwind Budget", "Northwind", "3.2", null, Script.Updates) { Tracked = false },
        new("Proseware.Draw", "Proseware Draw", "Proseware", "5.0", null, Script.Updates) { Tracked = false },
    ];

    private sealed record App(string Id, string Name, string Publisher, string? Installed, string? Available, Script Script)
    {
        public string? Installed { get; set; } = Installed;
        public string? Available { get; set; } = Available;
        public bool InCatalog { get; init; } = true;
        public bool Tracked { get; init; } = true;
        public string? Notes => Available is null ? null : $"https://example.com/notes/{Id.ToLowerInvariant()}";
    }
}
