using SoftwareUpdateTracker.Core.History;

namespace SoftwareUpdateTracker.Core.Storage;

public sealed class HistoryStore(string path, TimeProvider time)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly Lock _gate = new();
    private HistoryEntry[] _entries = [];
    private bool _unreadable;

    // Newest first, 90 days at most.
    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_gate) return Array.AsReadOnly(_entries); }
    }

    // True while history.json can't be read. History starts empty and the file isn't saved over.
    public bool Unreadable
    {
        get { lock (_gate) return _unreadable; }
    }

    // True when a corrupt file was set aside and history started empty.
    public bool Load()
    {
        lock (_gate) return Read() == FileState.Recovered;
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            EnsureReadable();
            Save(Prune([entry, .. _entries]));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            EnsureReadable();
            Save([]);
        }
    }

    private FileState Read()
    {
        var (file, state) = JsonFile.Load(path, CoreJson.Default.HistoryFile, () => new HistoryFile());
        _entries = Prune(file.Entries);
        _unreadable = state == FileState.Unreadable;
        return state;
    }

    // Tries the file again: the lock may have cleared.
    private void EnsureReadable()
    {
        if (_unreadable && Read() == FileState.Unreadable)
            throw new IOException($"{Path.GetFileName(path)} can't be read, so it isn't saved over.");
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
