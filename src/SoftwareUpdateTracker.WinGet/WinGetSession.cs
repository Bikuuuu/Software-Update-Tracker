using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;
using SoftwareUpdateTracker.Core.Checking;

namespace SoftwareUpdateTracker.WinGet;

// One connection to winget's COM server, for a single check, listing or upgrade.
// The server's vectors don't expose IIterable, so they're indexed, never enumerated.
public sealed class WinGetSession : IWinGetQueries
{
    public const string SourceName = "winget";
    private const int BatchSize = 100;
    private const uint SearchLimit = 500;

    private readonly PackageManager _manager;
    // Installed packages matched with the winget catalog, and the catalog alone.
    private readonly PackageCatalog _installed;
    private readonly PackageCatalog _catalog;

    private WinGetSession(PackageManager manager, PackageCatalog installed, PackageCatalog catalog, string version)
    {
        _manager = manager;
        _installed = installed;
        _catalog = catalog;
        Version = version;
    }

    public string Version { get; }

    // Throws PackageSourceException when winget is missing, too old or not answering.
    public static Task<WinGetSession> OpenAsync(CancellationToken ct) => Run(Open, ct);

    public Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct) =>
        Run(() => Installed(_installed.FindPackages(new FindPackagesOptions())), ct);

    public Task<IReadOnlyList<InstalledPackage>> FindInstalledByIdAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Batched(ids, batch => Installed(_installed.FindPackages(Selecting(PackageMatchField.Id, PackageFieldMatchOption.EqualsCaseInsensitive, batch))), ct);

    public Task<IReadOnlyList<InstalledPackage>> FindInstalledByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct) =>
        Batched(names, batch => Installed(_installed.FindPackages(Selecting(PackageMatchField.Name, PackageFieldMatchOption.EqualsCaseInsensitive, batch))), ct);

    public Task<IReadOnlyList<CatalogEntry>> FindCatalogByIdAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Batched(ids, batch => Entries(_catalog.FindPackages(Selecting(PackageMatchField.Id, PackageFieldMatchOption.EqualsCaseInsensitive, batch))), ct);

    public Task<IReadOnlyList<CatalogEntry>> SearchCatalogByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct) =>
        Batched(names, batch => Entries(_catalog.FindPackages(Selecting(PackageMatchField.Name, PackageFieldMatchOption.ContainsCaseInsensitive, batch, SearchLimit))), ct);

    private static WinGetSession Open()
    {
        var manager = new PackageManager();
        string version;
        try
        {
            version = manager.Version;
        }
        catch (InvalidCastException e)
        {
            throw new PackageSourceException(CheckProblem.WinGetTooOld, $"winget is older than {WinGetVersion.Minimum}.", e);
        }
        if (!WinGetVersion.IsSupported(version))
            throw new PackageSourceException(CheckProblem.WinGetTooOld, $"winget {version} is older than {WinGetVersion.Minimum}.");
        var options = new CreateCompositePackageCatalogOptions { CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs };
        options.Catalogs.Add(Reference(manager));
        return new WinGetSession(manager, Connect(manager.CreateCompositePackageCatalog(options)), Connect(Reference(manager)), version);
    }

    private static PackageCatalogReference Reference(PackageManager manager) =>
        manager.GetPackageCatalogByName(SourceName) ?? throw new PackageSourceException(CheckProblem.Failed, "The winget source isn't set up.");

    private static PackageCatalog Connect(PackageCatalogReference reference)
    {
        var result = reference.Connect();
        if (result.Status == ConnectResultStatus.Ok) return result.PackageCatalog;
        throw new PackageSourceException(CheckProblem.WinGetUnreachable, $"Can't open the winget catalog: {result.Status}.", result.ExtendedErrorCode);
    }

    private static IReadOnlyList<InstalledPackage> Installed(FindPackagesResult result)
    {
        var matches = Checked(result);
        var packages = new List<InstalledPackage>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var package = matches[i].CatalogPackage;
            var installed = package.InstalledVersion;
            if (installed is null) continue;
            var latest = package.DefaultInstallVersion;
            var update = latest is not null && package.IsUpdateAvailable;
            packages.Add(new InstalledPackage(
                installed.Id,
                package.Name,
                string.IsNullOrWhiteSpace(installed.Publisher) ? latest?.Publisher ?? "" : installed.Publisher,
                installed.Version,
                latest is null ? null : package.Id,
                latest?.DisplayName,
                latest?.Version,
                update,
                update ? Notes(latest!) : null));
        }
        return packages;
    }

    private static IReadOnlyList<CatalogEntry> Entries(FindPackagesResult result)
    {
        var matches = Checked(result);
        var entries = new List<CatalogEntry>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var package = matches[i].CatalogPackage;
            var latest = package.DefaultInstallVersion;
            entries.Add(new CatalogEntry(package.Id, latest?.DisplayName ?? package.Name, latest?.Version ?? ""));
        }
        return entries;
    }

    private static IReadOnlyList<MatchResult> Checked(FindPackagesResult result)
    {
        if (result.Status == FindPackagesResultStatus.Ok) return result.Matches;
        var problem = result.Status == FindPackagesResultStatus.CatalogError ? CheckProblem.WinGetUnreachable : CheckProblem.Failed;
        throw new PackageSourceException(problem, $"winget search failed: {result.Status}.", result.ExtendedErrorCode);
    }

    private static string? Notes(PackageVersionInfo latest)
    {
        try
        {
            return WebLink.Clean(latest.GetCatalogPackageMetadata().ReleaseNotesUrl);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static FindPackagesOptions Selecting(PackageMatchField field, PackageFieldMatchOption option, IEnumerable<string> values, uint limit = 0)
    {
        var options = new FindPackagesOptions { ResultLimit = limit };
        foreach (var value in values) options.Selectors.Add(new PackageMatchFilter { Field = field, Option = option, Value = value });
        return options;
    }

    private static async Task<IReadOnlyList<T>> Batched<T>(IReadOnlyCollection<string> values, Func<string[], IReadOnlyList<T>> query, CancellationToken ct)
    {
        var results = new List<T>();
        foreach (var batch in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchSize))
            results.AddRange(await Run(() => query(batch), ct));
        return results;
    }

    // COM calls run on the thread pool, and the wait ends when ct is cancelled even if winget hangs.
    private static async Task<T> Run<T>(Func<T> call, CancellationToken ct)
    {
        try
        {
            return await Task.Run(call, ct).WaitAsync(ct);
        }
        catch (Exception e) when (e is not (OperationCanceledException or PackageSourceException))
        {
            throw new PackageSourceException(ErrorMap.ForException(e), $"winget call failed: {e.Message}", e);
        }
    }
}
