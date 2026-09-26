using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Storage;

namespace SoftwareUpdateTracker.Presentation.Settings;

public sealed class SettingsWriter(SettingsStore store, FileLog log, Action<Action> post) : OrderedWriter(log, post)
{
    // done(false): the file couldn't be read or saved, and nothing changed.
    public void Update(Func<SettingsFile, SettingsFile> change, Action<bool>? done = null) =>
        Run("Settings", () => store.Update(change), done is null ? null : error => done(error is null));
}
