using TinyTracker.Core.Logging;
using TinyTracker.Core.Storage;

namespace TinyTracker.Presentation.Settings;

public sealed class SettingsWriter(SettingsStore store, FileLog log, Action<Action> post) : OrderedWriter(log, post)
{
    // done(false): the file couldn't be read or saved, and nothing changed.
    public void Update(Func<SettingsFile, SettingsFile> change, Action<bool>? done = null) =>
        Run("Settings", () => store.Update(change), done is null ? null : error => done(error is null));
}
