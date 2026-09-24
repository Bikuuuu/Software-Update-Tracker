using SoftwareUpdateTracker.Core.History;

namespace SoftwareUpdateTracker.Core.Storage;

public sealed class HistoryStore(string path, TimeProvider time)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly Lock _gate = new();
    private HistoryEntry[] _entries = [];

    // Newest first, 90 days at most.
    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_gate) return Array.AsReadOnly(_entries); }
    }

    // True when a corrupt file was set aside and history started empty.
    public bool Load()
    {
        lock (_gate)
        {
            var (file, recovered) = JsonFile.Load(path, CoreJson.Default.HistoryFile, () => new HistoryFile());
            _entries = Prune(file.Entries);
            return recovered;
        }
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate) Save(Prune([entry, .. _entries]));
    }

    public void Clear()
    {
        lock (_gate) Save([]);
    }

    private HistoryEntry[] Prune(IEnumerable<HistoryEntry> entries)
    {
        var cutoff = time.GetUtcNow() - Retention;
        return [.. entries.Where(e => e.Time >= cutoff).OrderByDescending(e => e.Time)];
    }

    // The store changes only once the file is saved.
    private void Save(HistoryEntry[] entries)
    {
        JsonFile.Save(path, new HistoryFile { Entries = entries }, CoreJson.Default.HistoryFile);
        _entries = entries;
    }
}
