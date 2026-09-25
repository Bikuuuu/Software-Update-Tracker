using Xunit;

namespace SoftwareUpdateTracker.WinGet.Tests.Integration;

public class WinGetSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(WinGetSession Session, InstalledPackage Matched)> OpenWithAMatchedPackage()
    {
        var session = await RealWinGet.OpenAsync();
        var listed = await session.ListInstalledAsync(Ct);
        return (session, listed.First(p => p.CatalogId is not null && !string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public async Task Open_ReportsASupportedVersion()
    {
        var session = await RealWinGet.OpenAsync();
        Assert.True(WinGetVersion.IsSupported(session.Version), session.Version);
    }

    [Fact]
    public async Task ListInstalled_HasLocalIdsAndCatalogMatches()
    {
        var session = await RealWinGet.OpenAsync();
        var listed = await session.ListInstalledAsync(Ct);
        Assert.NotEmpty(listed);
        Assert.All(listed, p => Assert.False(string.IsNullOrWhiteSpace(p.LocalId)));
        Assert.Contains(listed, p => p.CatalogId is not null);
    }

    [Fact]
    public async Task FindInstalledById_FindsAListedPackage()
    {
        var (session, matched) = await OpenWithAMatchedPackage();
        var found = await session.FindInstalledByIdAsync([matched.CatalogId!], Ct);
        Assert.Contains(found, p => string.Equals(p.LocalId, matched.LocalId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindInstalledByName_FindsAListedPackage()
    {
        var (session, matched) = await OpenWithAMatchedPackage();
        var found = await session.FindInstalledByNameAsync([matched.Name], Ct);
        Assert.Contains(found, p => string.Equals(p.CatalogId, matched.CatalogId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindCatalogById_KnowsRealIdsOnly()
    {
        var session = await RealWinGet.OpenAsync();
        var found = await session.FindCatalogByIdAsync(["Microsoft.PowerShell", "Nobody.NoSuchPackage.Anywhere"], Ct);
        Assert.Equal("Microsoft.PowerShell", Assert.Single(found).Id, ignoreCase: true);
    }

    [Fact]
    public async Task SearchCatalogByName_FindsPowerShell()
    {
        var session = await RealWinGet.OpenAsync();
        var found = await session.SearchCatalogByNameAsync(["PowerShell"], Ct);
        Assert.Contains(found, e => e.Id == "Microsoft.PowerShell");
        Assert.InRange(found.Count, 1, 500);
    }

    [Fact]
    public async Task ReleaseNotes_AreWebLinksOnly()
    {
        var session = await RealWinGet.OpenAsync();
        var notes = (await session.ListInstalledAsync(Ct)).Select(p => p.ReleaseNotesUrl).OfType<string>().ToList();
        Assert.All(notes, url => Assert.True(url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal)));
    }
}
