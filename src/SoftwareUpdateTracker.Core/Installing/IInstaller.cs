using SoftwareUpdateTracker.Core.Tracking;

namespace SoftwareUpdateTracker.Core.Installing;

// What the UI asks of the install queue.
public interface IInstaller
{
    void Enqueue(IEnumerable<InstallRequest> requests);

    void Cancel(PackageKey package);
}
