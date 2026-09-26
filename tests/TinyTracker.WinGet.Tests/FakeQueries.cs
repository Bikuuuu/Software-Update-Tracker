namespace TinyTracker.WinGet.Tests;

// Canned winget answers, with a log of the questions asked.
internal sealed class FakeQueries : IWinGetQueries
{
    public List<InstalledPackage> Listed { get; } = [];
    public List<InstalledPackage> ById { get; } = [];
    public List<(string Name, InstalledPackage Result)> ByName { get; } = [];
    public List<CatalogEntry> Catalog { get; } = [];
    public List<string> Asked { get; } = [];

    public static InstalledPackage Matched(string id, string name, string version, string? latest = null, bool update = false, string? localId = null) =>
        new(localId ?? $@"ARP\Machine\X64\{id}", name, "Example Publisher", version, id, name, latest ?? version, update, update ? "https://example.com/notes" : null);

    public Task<IReadOnlyList<InstalledPackage>> ListInstalledAsync(CancellationToken ct) => Answer("list", Listed);

    public Task<IReadOnlyList<InstalledPackage>> FindInstalledByIdAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Answer($"installed ids {string.Join(',', ids)}", ById.Where(p => ids.Contains(p.CatalogId!, StringComparer.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<InstalledPackage>> FindInstalledByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct) =>
        Answer($"installed names {string.Join(',', names)}", ByName.Where(n => names.Contains(n.Name, StringComparer.OrdinalIgnoreCase)).Select(n => n.Result));

    public Task<IReadOnlyList<CatalogEntry>> FindCatalogByIdAsync(IReadOnlyCollection<string> ids, CancellationToken ct) =>
        Answer($"catalog ids {string.Join(',', ids)}", Catalog.Where(e => ids.Contains(e.Id, StringComparer.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<CatalogEntry>> SearchCatalogByNameAsync(IReadOnlyCollection<string> names, CancellationToken ct) =>
        Answer($"catalog search {string.Join(',', names)}", Catalog.Where(e => names.Any(n => e.Name.Contains(n, StringComparison.OrdinalIgnoreCase))));

    private Task<IReadOnlyList<T>> Answer<T>(string question, IEnumerable<T> answer)
    {
        lock (Asked) Asked.Add(question);
        return Task.FromResult<IReadOnlyList<T>>([.. answer]);
    }
}
