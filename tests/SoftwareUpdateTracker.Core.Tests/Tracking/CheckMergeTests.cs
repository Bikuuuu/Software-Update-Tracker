using SoftwareUpdateTracker.Core.Tracking;
using Xunit;

namespace SoftwareUpdateTracker.Core.Tests.Tracking;

public class CheckMergeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly TrackedApp Firefox = new() { Id = "Mozilla.Firefox", Source = "winget", Name = "Firefox" };

    private static AppCheck MergeOne(TrackedApp app, PackageSnapshot? package, DateTimeOffset? now = null) =>
        Assert.Single(CheckMerge.Apply([app], package is null ? [] : [package], now ?? Now));

    private static PackageSnapshot Package(string installed, string? available, string id = "Mozilla.Firefox", string source = "winget") =>
        new(id, source, "Mozilla Firefox", installed, available);

    [Fact]
    public void OfferedUpdate_IsAvailableAndRecordsFirstSeen()
    {
        var check = MergeOne(Firefox, Package("130.0", "131.0"));
        Assert.Equal(AppStatus.Available, check.Status);
        Assert.True(check.NewVersion);
        Assert.Equal("131.0", check.App.Offer!.Version);
        Assert.Equal(Now, check.App.Offer.FirstSeen);
    }

    [Fact]
    public void SameOfferNextCheck_KeepsFirstSeenAndIsNotNew()
    {
        var first = MergeOne(Firefox, Package("130.0", "131.0"));
        var second = MergeOne(first.App, Package("130.0", "131.0"), Now.AddHours(6));
        Assert.Equal(AppStatus.Available, second.Status);
        Assert.False(second.NewVersion);
        Assert.Equal(Now, second.App.Offer!.FirstSeen);
    }

    [Fact]
    public void NewerOffer_StartsFreshBookkeeping()
    {
        var old = Firefox with
        {
            Offer = new Offer { Version = "131.0", FirstSeen = Now.AddDays(-3), LastAutoAttempt = Now.AddHours(-1), Phantom = true },
        };
        var check = MergeOne(old, Package("130.0", "132.0"));
        Assert.Equal(AppStatus.Available, check.Status);
        Assert.True(check.NewVersion);
        Assert.Equal(new Offer { Version = "132.0", FirstSeen = Now }, check.App.Offer);
    }

    [Fact]
    public void FirstSeenInTheFuture_IsResetToNow()
    {
        var old = Firefox with { Offer = new Offer { Version = "131.0", FirstSeen = Now.AddDays(2) } };
        Assert.Equal(Now, MergeOne(old, Package("130.0", "131.0")).App.Offer!.FirstSeen);
    }

    [Fact]
    public void NoUpdate_IsUpToDateAndClearsTheOffer()
    {
        var old = Firefox with { Offer = new Offer { Version = "131.0", FirstSeen = Now.AddDays(-1) } };
        var check = MergeOne(old, Package("131.0", null));
        Assert.Equal(AppStatus.UpToDate, check.Status);
        Assert.Null(check.App.Offer);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    public void UnknownInstalledVersion_IsVersionUnknown(string installed)
    {
        var check = MergeOne(Firefox, Package(installed, "131.0"));
        Assert.Equal(AppStatus.VersionUnknown, check.Status);
        Assert.False(check.NewVersion);
        Assert.Null(check.App.Offer);
    }

    [Fact]
    public void SkippedVersion_IsSkippedAndNotNew()
    {
        var check = MergeOne(Firefox with { SkippedVersion = "131.0" }, Package("130.0", "131.0"));
        Assert.Equal(AppStatus.Skipped, check.Status);
        Assert.False(check.NewVersion);
    }

    [Fact]
    public void VersionAfterTheSkippedOne_IsAvailable() =>
        Assert.Equal(AppStatus.Available, MergeOne(Firefox with { SkippedVersion = "131.0" }, Package("130.0", "132.0")).Status);

    [Fact]
    public void PhantomOffer_StaysPhantom()
    {
        var old = Firefox with { Offer = new Offer { Version = "131.0", FirstSeen = Now.AddDays(-1), Phantom = true } };
        Assert.Equal(AppStatus.Phantom, MergeOne(old, Package("130.0", "131.0")).Status);
    }

    [Fact]
    public void MissingPackage_IsNotFoundAndKeepsBookkeeping()
    {
        var old = Firefox with { Offer = new Offer { Version = "131.0", FirstSeen = Now.AddDays(-1) } };
        var check = MergeOne(old, null);
        Assert.Equal(AppStatus.NotFound, check.Status);
        Assert.Same(old, check.App);
    }

    [Fact]
    public void Id_MatchesIgnoringCase() =>
        Assert.Equal(AppStatus.Available, MergeOne(Firefox with { Id = "mozilla.firefox" }, Package("130.0", "131.0")).Status);

    [Fact]
    public void OtherSource_DoesNotMatch() =>
        Assert.Equal(AppStatus.NotFound, MergeOne(Firefox, Package("130.0", "131.0", source: "msstore")).Status);

    [Fact]
    public void Name_FollowsWinget() =>
        Assert.Equal("Mozilla Firefox", MergeOne(Firefox, Package("131.0", null)).App.Name);

    [Fact]
    public void Result_HasOneRowPerTrackedAppInOrder()
    {
        var vlc = new TrackedApp { Id = "VideoLAN.VLC", Source = "winget" };
        var checks = CheckMerge.Apply([vlc, Firefox], [Package("130.0", "131.0"), Package("1.0", null, id: "Other.App")], Now);
        Assert.Equal(["VideoLAN.VLC", "Mozilla.Firefox"], checks.Select(c => c.App.Id));
        Assert.Equal([AppStatus.NotFound, AppStatus.Available], checks.Select(c => c.Status));
    }
}
