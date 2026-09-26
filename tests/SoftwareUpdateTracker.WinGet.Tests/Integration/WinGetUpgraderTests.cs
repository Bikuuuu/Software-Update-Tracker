using SoftwareUpdateTracker.Core.Installing;
using SoftwareUpdateTracker.Core.Tracking;
using SoftwareUpdateTracker.Core.Versions;
using Xunit;

namespace SoftwareUpdateTracker.WinGet.Tests.Integration;

// Upgrades change what's installed, so no other test runs beside them.
[CollectionDefinition(DisableParallelization = true)]
public class UpgradeCollection;

// Real upgrades, only on GitHub runners, where the CI job installs the old versions first.
[Collection<UpgradeCollection>]
public class WinGetUpgraderTests
{
    private const string NotepadPlusPlus = "Notepad++.Notepad++";
    private const string OldNotepadPlusPlus = "8.9.7";
    private const string Vlc = "VideoLAN.VLC";
    private const string OldVlc = "3.0.20";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RunnerOnly() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("TINYTRACKER_WINGET_UPGRADE_TESTS") == "1",
            "Upgrade tests run only on GitHub runners.");

    private static async Task<InstalledPackage> InstalledAsync(string id)
    {
        var session = await WinGetSession.OpenAsync(Ct);
        return Assert.Single(await session.FindInstalledByIdAsync([id], Ct), p => string.Equals(p.CatalogId, id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OldPackage_UpgradesWithProgress()
    {
        RunnerOnly();
        var before = await InstalledAsync(NotepadPlusPlus);
        Assert.True(PackageVersion.Same(OldNotepadPlusPlus, before.Version), before.Version);
        var stages = new List<UpgradeStage>();
        var progress = new Reported<UpgradeProgress>(p =>
        {
            lock (stages) stages.Add(p.Stage);
        });

        var outcome = await new WinGetUpgrader().UpgradeAsync(new PackageKey(NotepadPlusPlus, "winget"), before.LatestVersion!, progress, Ct);

        Assert.True(outcome.Result is UpgradeResult.Updated or UpgradeResult.RestartNeeded, outcome.ToString());
        Assert.Contains(UpgradeStage.Downloading, stages);
        Assert.Contains(UpgradeStage.Installing, stages);
        Assert.True(PackageVersion.Same(before.LatestVersion, (await InstalledAsync(NotepadPlusPlus)).Version));
    }

    [Fact]
    public async Task CancelWhileDownloading_KeepsTheOldVersion()
    {
        RunnerOnly();
        var before = await InstalledAsync(Vlc);
        // The MSI reports 3.0.20 as 3.0.20.0, so compare versions, not text.
        Assert.True(PackageVersion.Same(OldVlc, before.Version), before.Version);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var fired = 0;
        // Cancel from another thread, not from inside winget's progress callback.
        var progress = new Reported<UpgradeProgress>(p =>
        {
            if (p.Stage == UpgradeStage.Downloading && p.BytesDownloaded > 0 && Interlocked.Exchange(ref fired, 1) == 0)
                _ = Task.Run(cancel.Cancel, Ct);
        });

        var outcome = await new WinGetUpgrader().UpgradeAsync(new PackageKey(Vlc, "winget"), before.LatestVersion!, progress, cancel.Token);

        Assert.Equal(UpgradeResult.Cancelled, outcome.Result);
        var after = await InstalledAsync(Vlc);
        Assert.True(PackageVersion.Same(OldVlc, after.Version), after.Version);
    }

    [Fact]
    public async Task VersionNotOffered_IsNoUpdate()
    {
        RunnerOnly();
        var outcome = await new WinGetUpgrader().UpgradeAsync(new PackageKey(NotepadPlusPlus, "winget"), "0.0.0.1", null, Ct);
        Assert.Equal(UpgradeResult.NoUpdate, outcome.Result);
    }

    [Fact]
    public async Task PackageNotInstalled_IsNotInstalled()
    {
        RunnerOnly();
        var outcome = await new WinGetUpgrader().UpgradeAsync(new PackageKey("Nobody.NoSuchPackage.Anywhere", "winget"), "1.0", null, Ct);
        Assert.Equal(UpgradeResult.NotInstalled, outcome.Result);
    }
}
