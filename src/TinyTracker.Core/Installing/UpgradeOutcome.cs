namespace TinyTracker.Core.Installing;

public enum UpgradeResult
{
    Updated,
    RestartNeeded,
    // Offer Close & update.
    AppInUse,
    NeedsAdmin,
    PermissionDeclined,
    // Another install is running; try again later.
    Busy,
    // winget doesn't offer this version anymore.
    NoUpdate,
    NotInstalled,
    Cancelled,
    Failed,
}

// Why an upgrade failed. The app words each reason in plain language.
public enum UpgradeFailure
{
    None,
    DownloadFailed,
    HashMismatch,
    NoNetwork,
    DiskFull,
    NotEnoughMemory,
    BlockedByPolicy,
    NoApplicableInstaller,
    NotSupported,
    MissingDependency,
    InstallerCancelled,
    NewerInstalled,
    InstallerFailed,
    WinGetUnavailable,
    // No download progress for two minutes, twice.
    Stalled,
    // Still running after 30 minutes.
    TookTooLong,
    Other,
}

// Code is the technical code behind Details, such as "0x8A150006, installer 1603".
public sealed record UpgradeOutcome(UpgradeResult Result, UpgradeFailure Failure = UpgradeFailure.None, string? Code = null);
