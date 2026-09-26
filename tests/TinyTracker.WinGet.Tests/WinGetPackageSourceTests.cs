using TinyTracker.Core.Checking;
using TinyTracker.Core.Tracking;
using Xunit;

namespace TinyTracker.WinGet.Tests;

public class WinGetPackageSourceTests
{
    private readonly FakeQueries _queries = new();
    private int _opens;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TrackedApp Tracked(string id, string source = "winget") => new() { Id = id, Source = source };

    private Task<CatalogRead> Read(params TrackedApp[] apps) =>
        new WinGetPackageSource(_ =>
        {
            _opens++;
            return Task.FromResult<IWinGetQueries>(_queries);
        }).ReadAsync(apps, Ct);

    [Fact]
    public async Task ListedApp_NeedsNoLookup()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0", latest: "131.0", update: true));
        var read = await Read(Tracked("Mozilla.Firefox"));
        Assert.Equal(
            new PackageSnapshot("Mozilla.Firefox", "winget", "Mozilla Firefox", "130.0", "131.0", "Example Publisher", "https://example.com/notes", @"ARP\Machine\X64\Mozilla.Firefox"),
            Assert.Single(read.Installed));
        Assert.Empty(read.NotInCatalog);
        Assert.Equal(["list"], _queries.Asked);
    }

    [Fact]
    public async Task NoUpdate_HasNoAvailableVersion()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "131.0"));
        Assert.Null(Assert.Single((await Read(Tracked("Mozilla.Firefox"))).Installed).AvailableVersion);
    }

    [Fact]
    public async Task AppTheListMissed_IsLookedUpById()
    {
        _queries.ById.Add(FakeQueries.Matched("Example.Editor", "Example Editor", "2.0", latest: "2.1", update: true));
        var read = await Read(Tracked("Example.Editor"));
        Assert.Equal("2.1", Assert.Single(read.Installed).AvailableVersion);
        Assert.Equal(["list", "installed ids Example.Editor"], _queries.Asked);
    }

    [Fact]
    public async Task AppNotInstalled_IsStillInTheCatalog()
    {
        _queries.Catalog.Add(new CatalogEntry("Example.Editor", "Example Editor", "2.1"));
        var read = await Read(Tracked("Example.Editor"));
        Assert.Empty(read.Installed);
        Assert.Empty(read.NotInCatalog);
        Assert.Equal(["list", "installed ids Example.Editor", "catalog ids Example.Editor"], _queries.Asked);
    }

    [Fact]
    public async Task AppGoneFromTheCatalog_IsNotInCatalog() =>
        Assert.Equal(new PackageKey("Example.Editor", "winget"), Assert.Single((await Read(Tracked("Example.Editor"))).NotInCatalog));

    [Fact]
    public async Task OtherSource_IsNotInCatalogWithoutAskingWinget()
    {
        var read = await Read(Tracked("XP0000000000", "msstore"));
        Assert.Equal(new PackageKey("XP0000000000", "msstore"), Assert.Single(read.NotInCatalog));
        Assert.Equal(0, _opens);
    }

    [Fact]
    public async Task IdCase_DoesNotMatter()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "131.0"));
        Assert.Equal("Mozilla.Firefox", Assert.Single((await Read(Tracked("mozilla.firefox"))).Installed).Id);
    }

    [Fact]
    public async Task SameIdInstalledTwice_PrefersTheOneWithAnUpdate()
    {
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "131.0", localId: @"ARP\User\X64\Mozilla Firefox"));
        _queries.Listed.Add(FakeQueries.Matched("Mozilla.Firefox", "Mozilla Firefox", "130.0", latest: "131.0", update: true));
        Assert.Equal("130.0", Assert.Single((await Read(Tracked("Mozilla.Firefox"))).Installed).InstalledVersion);
    }

    [Fact]
    public async Task WinGetFailure_Propagates()
    {
        var source = new WinGetPackageSource(_ => throw new PackageSourceException(CheckProblem.WinGetTooOld, "old"));
        var error = await Assert.ThrowsAsync<PackageSourceException>(() => source.ReadAsync([Tracked("Mozilla.Firefox")], Ct));
        Assert.Equal(CheckProblem.WinGetTooOld, error.Problem);
    }

    [Fact]
    public async Task MissingApps_AreLookedUpInOneCall()
    {
        await Read(Tracked("Example.Editor"), Tracked("Example.Viewer"));
        Assert.Contains("installed ids Example.Editor,Example.Viewer", _queries.Asked);
    }
}
