using SoftwareUpdateTracker.Core.Installing;
using SoftwareUpdateTracker.Core.Tracking;
using SoftwareUpdateTracker.Core.Versions;
using SoftwareUpdateTracker.Presentation.Text;

namespace SoftwareUpdateTracker.Presentation.Updates;

// The row states of spec §4.3, plus the up to date and skipped rows of the Up to date group.
public enum RowState
{
    Available,
    Waiting,
    Downloading,
    Installing,
    AppInUse,
    NeedsPermission,
    Failed,
    RestartNeeded,
    Updated,
    Phantom,
    VersionUnknown,
    NotFound,
    NotInCatalog,
    UpToDate,
    Skipped,
}

public enum RowAction
{
    None,
    Update,
    // Also offered for an app in use, until Close & update exists.
    Retry,
    UpdateAnyway,
    StopTracking,
}

// What one row shows, from the last check and the app's install. Pure, so every state is tested without the UI.
public sealed record RowView
{
    public required RowState State { get; init; }
    public required string Status { get; init; }
    public bool Warning { get; init; }
    public RowAction Action { get; init; }
    public bool CanCancel { get; init; }
    public bool ShowProgress { get; init; }
    public bool Indeterminate { get; init; }
    // 0 to 100.
    public double Percent { get; init; }
    public string? PercentText { get; init; }
    // "What's new" after the status line.
    public bool ShowNotes { get; init; }
    public string? Details { get; init; }
    // "2.4.1 → 2.5.0", with the changed part of the new version in the accent color.
    public string From { get; init; } = "";
    public VersionDiff To { get; init; }
    public bool ShowVersions { get; init; }
    public bool CanUpdateNow { get; init; }
    public bool CanSkip { get; init; }
    public bool HasNotes { get; init; }

    public bool HasAction => Action != RowAction.None;

    public string ActionText => Action switch
    {
        RowAction.Update => Strings.Update,
        RowAction.Retry => Strings.Retry,
        RowAction.UpdateAnyway => Strings.UpdateAnyway,
        RowAction.StopTracking => Strings.StopTracking,
        _ => "",
    };

    public bool HasDetails => Details is not null;
    public bool CanUndoSkip => State == RowState.Skipped;
    public bool InUpToDateGroup => State is RowState.UpToDate or RowState.Skipped or RowState.VersionUnknown;
    public bool IsActive => State is RowState.Waiting or RowState.Downloading or RowState.Installing;
    // Update all counts these (spec §4.3).
    public bool CountsForUpdateAll => State is RowState.Available or RowState.Failed;
    // The summary counts updates that aren't installed yet.
    public bool IsPending => State is RowState.Available or RowState.Waiting or RowState.Downloading or RowState.Installing
        or RowState.AppInUse or RowState.NeedsPermission or RowState.Failed;

    // In progress first, then rows that need attention, then available ones.
    public int Rank => State switch
    {
        RowState.Downloading or RowState.Installing or RowState.Updated => 0,
        RowState.Waiting => 1,
        RowState.Available => 3,
        _ => 2,
    };

    public static RowView Of(AppCheck check, InstallItem? install, DateTimeOffset now)
    {
        var view = install switch
        {
            { Stage: InstallStage.Waiting } => new RowView { State = RowState.Waiting, Status = install.Busy ? Strings.WaitingForOtherInstall : Strings.Waiting, CanCancel = true },
            { Stage: InstallStage.Downloading } => Downloading(install),
            { Stage: InstallStage.Installing } => Installing(install),
            { Done: { } done } => Finished(check, install, done) ?? FromCheck(check, now, done.Outcome.Result == UpgradeResult.PermissionDeclined),
            _ => FromCheck(check, now, false),
        };
        return WithVersions(view, check, install);
    }

    private static RowView Downloading(InstallItem install)
    {
        var progress = install.Progress;
        var known = progress.BytesRequired > 0 || progress.DownloadFraction > 0;
        var fraction = progress.BytesRequired > 0 ? Math.Clamp((double)progress.BytesDownloaded / progress.BytesRequired, 0, 1) : progress.DownloadFraction;
        var amount = Words.Downloaded(progress.BytesDownloaded, progress.BytesRequired);
        if (install.BytesPerSecond > 0) amount = Words.Joined(amount, Words.Speed(install.BytesPerSecond));
        return new RowView
        {
            State = RowState.Downloading,
            Status = Words.Format(Strings.Downloading, amount),
            CanCancel = progress.CanCancel,
            ShowProgress = true,
            Indeterminate = !known,
            Percent = fraction * 100,
            PercentText = known ? $"{(int)(fraction * 100)}%" : null,
        };
    }

    private static RowView Installing(InstallItem install)
    {
        var fraction = install.Progress.InstallFraction;
        return new RowView { State = RowState.Installing, Status = Strings.Installing, ShowProgress = true, Indeterminate = fraction <= 0, Percent = fraction * 100 };
    }

    // Null when the row goes back to what the check says.
    private static RowView? Finished(AppCheck check, InstallItem install, InstallDone done)
    {
        var details = done.Outcome.Code is { } code ? Words.Format(Strings.DetailsCode, code) : null;
        return done.Outcome.Result switch
        {
            UpgradeResult.Updated when done.Phantom => Phantom(),
            UpgradeResult.Updated => new RowView { State = RowState.Updated, Status = Words.Format(Strings.UpdatedTo, install.Request.ToVersion) },
            UpgradeResult.RestartNeeded => new RowView { State = RowState.RestartNeeded, Status = Strings.RestartToFinish },
            UpgradeResult.AppInUse => new RowView { State = RowState.AppInUse, Status = Words.Format(Strings.AppRunning, NameOf(check.App)), Warning = true, Action = RowAction.Retry, Details = details, CanUpdateNow = true },
            UpgradeResult.NeedsAdmin => new RowView { State = RowState.NeedsPermission, Status = Strings.NeedsAdmin, Warning = true, Details = details },
            UpgradeResult.NotInstalled => new RowView { State = RowState.NotFound, Status = Strings.NotInstalledAnymore, Action = RowAction.StopTracking },
            UpgradeResult.Cancelled or UpgradeResult.PermissionDeclined => null,
            _ => new RowView { State = RowState.Failed, Status = Words.Reason(done.Reason), Warning = true, Action = RowAction.Retry, Details = details, CanUpdateNow = true },
        };
    }

    private static RowView FromCheck(AppCheck check, DateTimeOffset now, bool declined) => check.Status switch
    {
        AppStatus.Available => new RowView
        {
            State = RowState.Available,
            Status = declined ? Strings.PermissionDeclined : Released(check.App.Offer!, now),
            ShowNotes = !declined && check.Package?.ReleaseNotesUrl is not null,
            Action = RowAction.Update,
            CanUpdateNow = true,
        },
        AppStatus.Phantom => Phantom(),
        AppStatus.Skipped => new RowView { State = RowState.Skipped, Status = Words.Format(Strings.SkippedVersion, check.App.SkippedVersion), CanUpdateNow = true },
        AppStatus.VersionUnknown => new RowView { State = RowState.VersionUnknown, Status = Strings.VersionUnknown },
        AppStatus.NotFound => new RowView { State = RowState.NotFound, Status = Strings.NotInstalledAnymore, Action = RowAction.StopTracking },
        AppStatus.NotInCatalog => new RowView { State = RowState.NotInCatalog, Status = Strings.NotFoundInWinGet, Action = RowAction.StopTracking },
        _ => new RowView { State = RowState.UpToDate, Status = check.Package?.InstalledVersion ?? "" },
    };

    private static RowView Phantom() => new() { State = RowState.Phantom, Status = Strings.PhantomStatus, Action = RowAction.UpdateAnyway };

    // The versions come from the install while there is one, else from the check's offer.
    private static RowView WithVersions(RowView view, AppCheck check, InstallItem? install)
    {
        var from = install?.Request.FromVersion ?? check.Package?.InstalledVersion ?? "";
        var to = install?.Request.ToVersion ?? check.App.Offer?.Version;
        var shows = to is not null && view.State is not (RowState.UpToDate or RowState.Skipped or RowState.VersionUnknown or RowState.NotFound or RowState.NotInCatalog);
        var offered = check.App.Offer is not null && check.Status is AppStatus.Available or AppStatus.Phantom or AppStatus.Skipped;
        return view with
        {
            From = shows ? from : "",
            To = shows ? VersionDiff.Between(from, to) : default,
            ShowVersions = shows,
            CanSkip = offered && check.Status != AppStatus.Skipped && !view.IsActive && view.State is not (RowState.Updated or RowState.RestartNeeded),
            CanUpdateNow = view.CanUpdateNow && offered && !view.IsActive,
            HasNotes = offered && check.Package?.ReleaseNotesUrl is not null,
        };
    }

    // The release date when known and not ahead of now, else the day the version was first seen.
    private static string Released(Offer offer, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var day = offer.ReleaseDate is { } date && date <= today ? date : DateOnly.FromDateTime(offer.FirstSeen.UtcDateTime);
        return Words.Released(day, now);
    }

    private static string NameOf(TrackedApp app) => app.Name.Length > 0 ? app.Name : app.Id;
}
