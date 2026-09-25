using SoftwareUpdateTracker.Core.Versions;

namespace SoftwareUpdateTracker.WinGet.Matching;

// Which lookup matches are safe to offer. Targeted lookups correlate loosely,
// and a wrong match could install another edition or track over the app.
public static class Matcher
{
    public static IReadOnlyDictionary<string, InstalledPackage> Accept(
        IReadOnlyList<InstalledPackage> unmatched,
        IReadOnlySet<string> listedIds,
        IReadOnlyList<InstalledPackage> found)
    {
        var entries = unmatched
            .GroupBy(p => p.LocalId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var best = found
            .Where(f => f.CatalogId is not null && !listedIds.Contains(f.CatalogId))
            .Where(f => entries.TryGetValue(f.LocalId, out var entry) && Fits(entry, f))
            .DistinctBy(f => (f.LocalId.ToUpperInvariant(), f.CatalogId!.ToUpperInvariant()))
            .GroupBy(f => f.LocalId, StringComparer.OrdinalIgnoreCase)
            .Select(g => Best(entries[g.Key], [.. g]))
            .OfType<InstalledPackage>()
            .ToList();
        // One catalog package for two installed apps is ambiguous too.
        return best
            .GroupBy(f => f.CatalogId!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .ToDictionary(f => f.LocalId, StringComparer.OrdinalIgnoreCase);
    }

    // Same name, a known version, and not an older track than the installed one.
    private static bool Fits(InstalledPackage entry, InstalledPackage match)
    {
        var installed = PackageVersion.Parse(entry.Version);
        var latest = PackageVersion.Parse(match.LatestVersion);
        var name = NameKey.Of(entry.Name);
        return !installed.IsUnknown && !latest.IsUnknown && latest.CompareTo(installed) >= 0
            && name.Length > 0 && name == NameKey.Of(match.CatalogName ?? match.Name);
    }

    // Several fits: the one whose version shares the most leading parts with the installed one, unless tied.
    private static InstalledPackage? Best(InstalledPackage entry, IReadOnlyList<InstalledPackage> fits)
    {
        if (fits.Count == 1) return fits[0];
        var scored = fits.Select(f => (Match: f, Score: SharedParts(f.LatestVersion!, entry.Version))).ToList();
        var top = scored.Max(s => s.Score);
        var winners = scored.Where(s => s.Score == top).ToList();
        return winners.Count == 1 ? winners[0].Match : null;
    }

    private static int SharedParts(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        var shared = 0;
        while (shared < left.Length && shared < right.Length && Part(left[shared]) == Part(right[shared])) shared++;
        return shared;
    }

    private static string Part(string part)
    {
        var trimmed = part.Trim().TrimStart('0');
        return (trimmed.Length == 0 ? "0" : trimmed).ToUpperInvariant();
    }
}
