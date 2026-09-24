namespace SoftwareUpdateTracker.Core.Storage;

public sealed class SettingsStore(string path)
{
    private readonly Lock _gate = new();
    private SettingsFile _current = new();

    public SettingsFile Current
    {
        get { lock (_gate) return _current; }
    }

    // True when a corrupt file was set aside and defaults were loaded; the app shows a notice.
    public bool Load()
    {
        lock (_gate)
        {
            var (file, recovered) = JsonFile.Load(path, CoreJson.Default.SettingsFile, () => new SettingsFile());
            _current = file.Normalize();
            return recovered;
        }
    }

    // Changes are applied and saved one at a time.
    public SettingsFile Update(Func<SettingsFile, SettingsFile> change)
    {
        lock (_gate)
        {
            var next = change(_current).Normalize();
            JsonFile.Save(path, next, CoreJson.Default.SettingsFile);
            _current = next;
            return next;
        }
    }
}
