using SoftwareUpdateTracker.Core.Tracking;

namespace SoftwareUpdateTracker.Core.Installing;

public interface IPackageUpgrader
{
    // Upgrades to exactly this version. Cancelling stops a queued or downloading upgrade and returns Cancelled;
    // once the installer runs, it finishes and the real result comes back. Never throws.
    Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct);
}
