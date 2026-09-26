using SoftwareUpdateTracker.Core.Installing;
using SoftwareUpdateTracker.Core.Tracking;
using SoftwareUpdateTracker.Presentation.Updates;
using Xunit;
using static SoftwareUpdateTracker.Presentation.Tests.Fixtures;

namespace SoftwareUpdateTracker.Presentation.Tests.Updates;

// Every row state of spec §4.3: its status line, action and progress.
public class RowViewTests
{
    private static RowView View(AppCheck check, InstallItem? install = null) => RowView.Of(check, install, Now);

    [Fact]
    public void Available_ShowsItsAge_AnUpdateButton_AndWhatsNew()
    {
        var view = View(Check(AppStatus.Available));
        Assert.Equal((RowState.Available, "2 days ago", RowAction.Update, "Update"), (view.State, view.Status, view.Action, view.ActionText));
        Assert.True(view.ShowNotes);
        Assert.Equal(("2.4.1", "2.", "5.0"), (view.From, view.To.Unchanged, view.To.Changed));
        Assert.True(view.ShowVersions && view.CanUpdateNow && view.CanSkip && view.HasNotes);
        Assert.False(view.ShowProgress || view.CanCancel || view.Warning);
    }

    [Fact]
    public void Available_UsesTheReleaseDateWhenKnown()
    {
        var check = Check(AppStatus.Available);
        check = check with { App = check.App with { Offer = check.App.Offer! with { ReleaseDate = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-5) } } };
        Assert.Equal("5 days ago", View(check).Status);
    }

    [Fact]
    public void Available_WithoutNotes_HidesWhatsNew()
    {
        var view = View(Check(AppStatus.Available, notes: null));
        Assert.False(view.ShowNotes || view.HasNotes);
    }

    [Fact]
    public void Waiting_CanBeCancelled()
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.Waiting));
        Assert.Equal((RowState.Waiting, "Waiting…", RowAction.None), (view.State, view.Status, view.Action));
        Assert.True(view.CanCancel && view.IsActive);
        Assert.False(view.CanUpdateNow || view.CanSkip);
    }

    [Fact]
    public void WaitingForAnotherInstall_SaysSo() =>
        Assert.Equal("Waiting for another install to finish…", View(Check(AppStatus.Available), Item(InstallStage.Waiting, busy: true)).Status);

    [Fact]
    public void Downloading_ShowsTheAmountSpeedAndPercent()
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.Downloading, progress: Downloading(180 * MB, 400 * MB), speed: 20 * MB));
        Assert.Equal((RowState.Downloading, $"{Nb("180 of 400 MB")} · {Nb("20 MB/s")}", "45%"), (view.State, view.Status, view.PercentText));
        Assert.Equal(45, view.Percent, 3);
        Assert.True(view.ShowProgress && view.CanCancel);
        Assert.False(view.Indeterminate);
    }

    [Fact]
    public void Downloading_OfUnknownSize_HasNoPercent()
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.Downloading, progress: Downloading(180 * MB, 0)));
        Assert.Equal((Nb("180 MB"), null), (view.Status, view.PercentText));
        Assert.True(view.Indeterminate);
    }

    [Theory]
    [InlineData(0.0, true, 0.0)]
    [InlineData(0.5, false, 50.0)]
    public void Installing_ShowsProgressWhenKnown(double fraction, bool indeterminate, double percent)
    {
        var view = View(Check(AppStatus.Available), Item(InstallStage.Installing, progress: new UpgradeProgress(UpgradeStage.Installing, 0, 0, 1, fraction)));
        Assert.Equal((RowState.Installing, "Installing…", indeterminate, RowAction.None), (view.State, view.Status, view.Indeterminate, view.Action));
        Assert.Equal(percent, view.Percent, 3);
        Assert.False(view.CanCancel);
    }

    [Fact]
    public void AppInUse_NamesTheApp_AndOffersRetry()
    {
        var view = View(Check(AppStatus.Available), Done(UpgradeResult.AppInUse, code: "0x8A150101"));
        Assert.Equal((RowState.AppInUse, "Example Editor is running", RowAction.Retry, "Code: 0x8A150101"), (view.State, view.Status, view.Action, view.Details));
        Assert.True(view.Warning && view.HasDetails && view.IsPending);
    }

    [Fact]
    public void NeedsAdmin_SaysSo_WithoutAnAction()
    {
        var view = View(Check(AppStatus.Available), Done(UpgradeResult.NeedsAdmin, code: "0x8A150019"));
        Assert.Equal((RowState.NeedsPermission, "Needs admin permission", RowAction.None), (view.State, view.Status, view.Action));
    }

    [Theory]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.DiskFull, "Not enough disk space")]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.Stalled, "Download stalled")]
    [InlineData(UpgradeResult.Failed, UpgradeFailure.TookTooLong, "Took too long")]
    [InlineData(UpgradeResult.Busy, UpgradeFailure.None, "Another install is running")]
    [InlineData(UpgradeResult.NoUpdate, UpgradeFailure.None, "This version isn't offered anymore")]
    public void Failed_GivesTheReason_Retry_AndDetails(UpgradeResult result, UpgradeFailure failure, string reason)
    {
        var view = View(Check(AppStatus.Available), Done(result, failure: failure, code: "0x8A150105"));
        Assert.Equal((RowState.Failed, reason, RowAction.Retry, "Code: 0x8A150105"), (view.State, view.Status, view.Action, view.Details));
        Assert.True(view.CountsForUpdateAll && view.CanUpdateNow && view.CanSkip);
    }

    [Fact]
    public void RestartNeeded_HasNoAction()
    {
        var view = View(Check(AppStatus.Available), Done(UpgradeResult.RestartNeeded));
        Assert.Equal((RowState.RestartNeeded, "Restart to finish", RowAction.None), (view.State, view.Status, view.Action));
        Assert.False(view.IsPending || view.CanSkip);
    }

    [Fact]
    public void Updated_ShowsTheNewVersion_WhileTheCheckIsAlreadyUpToDate()
    {
        var view = View(Check(AppStatus.UpToDate, installed: "2.5.0", offer: null), Done(UpgradeResult.Updated));
        Assert.Equal((RowState.Updated, "Updated to 2.5.0", RowAction.None), (view.State, view.Status, view.Action));
        Assert.Equal(("2.4.1", "5.0"), (view.From, view.To.Changed));
        Assert.False(view.InUpToDateGroup);
    }

    [Fact]
    public void Phantom_OffersUpdateAnyway()
    {
        foreach (var view in new[] { View(Check(AppStatus.Available), Done(UpgradeResult.Updated, phantom: true)), View(Check(AppStatus.Phantom)) })
        {
            Assert.Equal((RowState.Phantom, "Installed, but Windows still reports the old version", RowAction.UpdateAnyway), (view.State, view.Status, view.Action));
            Assert.False(view.CountsForUpdateAll || view.IsPending);
        }
    }

    [Fact]
    public void VersionUnknown_IsInTheUpToDateGroup()
    {
        var view = View(Check(AppStatus.VersionUnknown, installed: "Unknown", offer: null));
        Assert.Equal((RowState.VersionUnknown, "Version unknown · this app updates itself", RowAction.None), (view.State, view.Status, view.Action));
        Assert.True(view.InUpToDateGroup);
    }

    [Theory]
    [InlineData(AppStatus.NotFound, RowState.NotFound, "Not installed anymore")]
    [InlineData(AppStatus.NotInCatalog, RowState.NotInCatalog, "Not found in winget")]
    public void Missing_OffersStopTracking(AppStatus status, RowState state, string text)
    {
        var view = View(Check(status, offer: null));
        Assert.Equal((state, text, RowAction.StopTracking, "Stop tracking"), (view.State, view.Status, view.Action, view.ActionText));
        Assert.False(view.ShowVersions || view.InUpToDateGroup);
    }

    [Fact]
    public void UpToDate_ShowsTheInstalledVersion()
    {
        var view = View(Check(AppStatus.UpToDate, installed: "2.5.0", offer: null));
        Assert.Equal((RowState.UpToDate, "2.5.0", RowAction.None), (view.State, view.Status, view.Action));
        Assert.True(view.InUpToDateGroup);
        Assert.False(view.ShowVersions || view.CanSkip || view.CanUpdateNow);
    }

    [Fact]
    public void Skipped_CanBeUndone()
    {
        var view = View(Check(AppStatus.Skipped, skipped: "2.5.0"));
        Assert.Equal((RowState.Skipped, "Skipped version 2.5.0"), (view.State, view.Status));
        Assert.True(view.CanUndoSkip && view.CanUpdateNow && view.InUpToDateGroup);
        Assert.False(view.CanSkip || view.CountsForUpdateAll);
    }

    [Fact]
    public void PermissionDeclined_ReturnsToAvailable_WithANote()
    {
        var view = View(Check(AppStatus.Available), Done(UpgradeResult.PermissionDeclined));
        Assert.Equal((RowState.Available, "Permission was declined", RowAction.Update), (view.State, view.Status, view.Action));
        Assert.False(view.ShowNotes);
    }

    [Fact]
    public void Cancelled_ReturnsToAvailable() =>
        Assert.Equal((RowState.Available, "2 days ago"), (View(Check(AppStatus.Available), Done(UpgradeResult.Cancelled)).State, View(Check(AppStatus.Available), Done(UpgradeResult.Cancelled)).Status));

    [Fact]
    public void NotInstalledResult_IsNotFound() =>
        Assert.Equal(RowState.NotFound, View(Check(AppStatus.Available), Done(UpgradeResult.NotInstalled)).State);

    [Fact]
    public void Rank_PutsWorkFirst_ThenAttention_ThenAvailable()
    {
        var available = View(Check(AppStatus.Available));
        var failed = View(Check(AppStatus.Available), Done(UpgradeResult.Failed, failure: UpgradeFailure.Other));
        var waiting = View(Check(AppStatus.Available), Item(InstallStage.Waiting));
        var downloading = View(Check(AppStatus.Available), Item(InstallStage.Downloading, progress: Downloading(1, 2)));
        Assert.Equal([downloading, waiting, failed, available], new[] { available, failed, waiting, downloading }.OrderBy(v => v.Rank));
    }
}
