using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Core.Storage;

namespace SoftwareUpdateTracker.Presentation.Settings;

// Saves settings changes off the UI thread, one at a time and in order, because a save can wait on rename retries.
// Each result comes back on the UI thread.
public sealed class SettingsWriter(SettingsStore store, FileLog log, Action<Action> post)
{
    private readonly Lock _gate = new();
    private Task _last = Task.CompletedTask;

    // Completes once every change so far is saved or refused.
    public Task Idle
    {
        get { lock (_gate) return _last; }
    }

    // done(false): the file couldn't be read or saved, and nothing changed.
    public void Update(Func<SettingsFile, SettingsFile> change, Action<bool>? done = null)
    {
        lock (_gate) _last = _last.ContinueWith(_ => Save(change, done), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Save(Func<SettingsFile, SettingsFile> change, Action<bool>? done)
    {
        var saved = false;
        try
        {
            store.Update(change);
            saved = true;
        }
        catch (IOException e)
        {
            log.Warn($"Settings not saved: {e.Message}");
        }
        catch (Exception e)
        {
            log.Error("Settings change failed", e);
        }
        if (done is not null) post(() => done(saved));
    }
}
