using Microsoft.Management.Deployment;

PackageManager manager;
try
{
    manager = new PackageManager();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"winget COM API unavailable: 0x{ex.HResult:X8} {ex.Message}");
    return 3;
}

string version;
try
{
    version = manager.Version;
}
catch (InvalidCastException)
{
    Console.Error.WriteLine("winget is too old for this COM API (IPackageManager7 missing); update App Installer.");
    return 4;
}
Console.WriteLine($"winget COM {version}");
var catalog = Connect(manager);
switch (args.FirstOrDefault() ?? "list")
{
    case "list":
        List(catalog);
        return 0;
    case "upgrade" when args.Length == 2:
        return await Upgrade(manager, catalog, args[1], null);
    case "cancel" when args.Length == 3:
        return await Upgrade(manager, catalog, args[1], int.Parse(args[2]));
    default:
        Console.Error.WriteLine("usage: list | upgrade <id> | cancel <id> <milliseconds>");
        return 2;
}

static PackageCatalog Connect(PackageManager manager)
{
    foreach (var names in new[] { new[] { "winget", "msstore" }, new[] { "winget" } })
    {
        var options = new CreateCompositePackageCatalogOptions { CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs };
        foreach (var name in names)
        {
            var reference = manager.GetPackageCatalogByName(name);
            if (reference is not null) options.Catalogs.Add(reference);
        }
        var result = manager.CreateCompositePackageCatalog(options).Connect();
        if (result.Status == ConnectResultStatus.Ok) return result.PackageCatalog;
        Console.WriteLine($"connect {string.Join('+', names)}: {result.Status}");
    }
    throw new InvalidOperationException("Could not connect to the winget catalogs.");
}

static void List(PackageCatalog catalog)
{
    var matches = catalog.FindPackages(new FindPackagesOptions()).Matches;
    var withUpdates = 0;
    // The out-of-proc COM vectors don't expose IIterable, so index instead of foreach.
    for (var i = 0; i < matches.Count; i++)
    {
        var package = matches[i].CatalogPackage;
        var latest = package.DefaultInstallVersion;
        if (latest is null) continue;
        var installed = package.InstalledVersion?.Version ?? "?";
        var line = $"{package.Id} | {package.Name} | {installed}";
        if (package.IsUpdateAvailable && installed != "Unknown")
        {
            withUpdates++;
            var installer = latest.GetApplicableInstaller(new InstallOptions());
            var notes = latest.GetCatalogPackageMetadata().ReleaseNotesUrl;
            line += $" -> {latest.Version} | scope={installer?.Scope} elevation={installer?.ElevationRequirement} | notes={notes}";
        }
        Console.WriteLine(line);
    }
    Console.WriteLine($"TOTAL {matches.Count} correlated, {withUpdates} with updates");
}

static async Task<int> Upgrade(PackageManager manager, PackageCatalog catalog, string id, int? cancelAfterMs)
{
    var find = new FindPackagesOptions();
    find.Filters.Add(new PackageMatchFilter { Field = PackageMatchField.Id, Option = PackageFieldMatchOption.Equals, Value = id });
    var found = catalog.FindPackages(find).Matches;
    if (found.Count != 1) throw new InvalidOperationException($"Expected one installed match for {id}, found {found.Count}.");
    var package = found[0].CatalogPackage;
    Console.WriteLine($"upgrading {package.Id} {package.InstalledVersion?.Version} -> {package.DefaultInstallVersion?.Version}");
    var operation = manager.UpgradePackageAsync(package, new InstallOptions { PackageInstallMode = PackageInstallMode.Silent, AcceptPackageAgreements = true });
    operation.Progress = (_, p) => Console.WriteLine($"PROGRESS {p.State} {p.BytesDownloaded}/{p.BytesRequired} download={p.DownloadProgress:F2} install={p.InstallationProgress:F2}");
    if (cancelAfterMs is int delay)
        _ = Task.Delay(delay).ContinueWith(_ => { Console.WriteLine("CANCEL"); operation.Cancel(); }, TaskScheduler.Default);
    try
    {
        var result = await operation;
        Console.WriteLine($"RESULT {result.Status} extended=0x{(result.ExtendedErrorCode?.HResult ?? 0):X8} installer={result.InstallerErrorCode} reboot={result.RebootRequired}");
        return result.Status == InstallResultStatus.Ok ? 0 : 1;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("RESULT Cancelled");
        return cancelAfterMs is null ? 1 : 0;
    }
}
