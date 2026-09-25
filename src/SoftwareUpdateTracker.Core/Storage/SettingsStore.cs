namespace SoftwareUpdateTracker.Core.Storage;

public sealed class SettingsStore(string path)
{
    private readonly Lock _gate = new();
    private SettingsFile _current = new();
    private bool _unreadable;

    public SettingsFile Current
    {
        get { lock (_gate) return _current; }
    }

    // True while settings.json can't be read (locked, denied, a folder). Defaults are in use and the file isn't saved over.
    public bool Unreadable
    {
        get { lock (_gate) return _unreadable; }
    }

    // True when a corrupt file was set aside and defaults were loaded; the app shows a notice.
    public bool Load()
    {
        lock (_gate) return Read() == FileState.Recovered;
    }

    // Changes are applied and saved one at a time. Throws IOException when the file can't be read or saved; nothing changes then.
    public SettingsFile Update(Func<SettingsFile, SettingsFile> change)
    {
        lock (_gate)
        {
            EnsureReadable();
            var next = change(_current).Normalize();
            JsonFile.Save(path, next, CoreJson.Default.SettingsFile);
            _current = next;
            return next;
        }
    }

    private FileState Read()
    {
        var (file, state) = JsonFile.Load(path, CoreJson.Default.SettingsFile, () => new SettingsFile());
        _current = file.Normalize();
        _unreadable = state == FileState.Unreadable;
        return state;
    }

    // Tries the file again: the lock may have cleared.
    private void EnsureReadable()
    {
        if (_unreadable && Read() == FileState.Unreadable)
            throw new IOException($"{Path.GetFileName(path)} can't be read, so it isn't saved over.");
    }
}
