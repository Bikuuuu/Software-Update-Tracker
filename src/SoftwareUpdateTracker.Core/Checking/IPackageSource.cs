using SoftwareUpdateTracker.Core.Tracking;

namespace SoftwareUpdateTracker.Core.Checking;

// Installed: the tracked apps found installed. NotInCatalog: tracked apps the catalog no longer has.
public sealed record CatalogRead(IReadOnlyList<PackageSnapshot> Installed, IReadOnlyList<PackageKey> NotInCatalog);

// Reads the tracked apps from winget. Throws PackageSourceException when winget can't answer.
public interface IPackageSource
{
    Task<CatalogRead> ReadAsync(IReadOnlyList<TrackedApp> apps, CancellationToken ct);
}
