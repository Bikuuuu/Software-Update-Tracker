using Xunit;

namespace SoftwareUpdateTracker.WinGet.Tests.Integration;

// Asserts over installed packages print no package data: a failure must not reveal installed apps.
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
        var allHaveLocalIds = listed.All(p => !string.IsNullOrWhiteSpace(p.LocalId));
        var someMatched = listed.Any(p => p.CatalogId is not null);
        Assert.NotEmpty(listed);
        Assert.True(allHaveLocalIds, "Some installed packages have no local id.");
        Assert.True(someMatched, "No installed package matched the catalog.");
    }

    [Fact]
    public async Task FindInstalledById_FindsAListedPackage()
    {
        var (session, matched) = await OpenWithAMatchedPackage();
        var found = await session.FindInstalledByIdAsync([matched.CatalogId!], Ct);
        var again = found.Any(p => string.Equals(p.LocalId, matched.LocalId, StringComparison.OrdinalIgnoreCase));
        Assert.True(again, "The listed package wasn't found by its id.");
    }

    [Fact]
    public async Task FindInstalledByName_FindsAListedPackage()
    {
        var (session, matched) = await OpenWithAMatchedPackage();
        var found = await session.FindInstalledByNameAsync([matched.Name], Ct);
        var again = found.Any(p => string.Equals(p.CatalogId, matched.CatalogId, StringComparison.OrdinalIgnoreCase));
        Assert.True(again, "The listed package wasn't found by its name.");
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
        var others = notes.Count(url => !url.StartsWith("https://", StringComparison.Ordinal) && !url.StartsWith("http://", StringComparison.Ordinal));
        Assert.Equal(0, others);
    }
}
