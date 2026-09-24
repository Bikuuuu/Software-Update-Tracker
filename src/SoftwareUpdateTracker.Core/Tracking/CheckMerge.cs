using SoftwareUpdateTracker.Core.Versions;

namespace SoftwareUpdateTracker.Core.Tracking;

// NewVersion: this check is the first to offer the version (one toast per version).
public sealed record AppCheck(TrackedApp App, AppStatus Status, PackageSnapshot? Package, bool NewVersion);

public static class CheckMerge
{
    public static IReadOnlyList<AppCheck> Apply(IReadOnlyList<TrackedApp> apps, IReadOnlyList<PackageSnapshot> packages, DateTimeOffset now) =>
        apps.Select(app => Merge(app, packages.FirstOrDefault(p => app.Matches(p.Id, p.Source)), now)).ToList();

    private static AppCheck Merge(TrackedApp app, PackageSnapshot? package, DateTimeOffset now)
    {
        // Missing or unknown tells us nothing new, so bookkeeping stays.
        if (package is null) return new AppCheck(app, AppStatus.NotFound, null, false);
        if (package.Name.Length > 0) app = app with { Name = package.Name };
        if (PackageVersion.Parse(package.InstalledVersion).IsUnknown) return new AppCheck(app, AppStatus.VersionUnknown, package, false);
        if (PackageVersion.Parse(package.AvailableVersion).IsUnknown)
            return new AppCheck(app with { Offer = null }, AppStatus.UpToDate, package, false);

        var available = package.AvailableVersion!;
        var isNew = app.Offer is null || !PackageVersion.Same(app.Offer.Version, available);
        var offer = isNew ? new Offer { Version = available, FirstSeen = now } : app.Offer!;
        // First seen after now means the clock went back.
        if (offer.FirstSeen > now) offer = offer with { FirstSeen = now };
        app = app with { Offer = offer };
        if (PackageVersion.Same(app.SkippedVersion, available)) return new AppCheck(app, AppStatus.Skipped, package, false);
        if (offer.Phantom) return new AppCheck(app, AppStatus.Phantom, package, false);
        return new AppCheck(app, AppStatus.Available, package, isNew);
    }
}
