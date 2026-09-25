using System.Collections.Concurrent;
using SoftwareUpdateTracker.Core.Installing;
using SoftwareUpdateTracker.Core.Tracking;

namespace SoftwareUpdateTracker.Presentation.Tests;

// Example apps, checks and installs for the view-model tests.
internal static class Fixtures
{
    public const ulong MB = 1024 * 1024;
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    public static TrackedApp App(string id = "Example.Editor", string? offer = "2.5.0", string? skipped = null, bool auto = false, bool phantom = false) => new()
    {
        Id = id,
        Source = "winget",
        Name = Name(id),
        Auto = auto,
        SkippedVersion = skipped,
        Offer = offer is null ? null : new Offer { Version = offer, FirstSeen = Now - TimeSpan.FromDays(2), Phantom = phantom },
    };

    public static PackageSnapshot Package(string id = "Example.Editor", string installed = "2.4.1", string? available = "2.5.0", string? notes = "https://example.com/notes") =>
        new(id, "winget", Name(id), installed, available, "Example Publisher", notes, $@"ARP\Machine\X64\{Name(id)}");

    public static AppCheck Check(AppStatus status, string id = "Example.Editor", string installed = "2.4.1", string? offer = "2.5.0", string? skipped = null, bool auto = false, string? notes = "https://example.com/notes") =>
        new(App(id, offer, skipped, auto, status == AppStatus.Phantom), status, status is AppStatus.NotFound or AppStatus.NotInCatalog ? null : Package(id, installed, offer, notes), false);

    public static InstallRequest Request(string id = "Example.Editor", string from = "2.4.1", string to = "2.5.0") => new(new PackageKey(id, "winget"), Name(id), from, to);

    public static InstallItem Item(InstallStage stage, string id = "Example.Editor", UpgradeProgress progress = default, double speed = 0, bool busy = false) =>
        new(Request(id), stage) { Progress = progress, BytesPerSecond = speed, Busy = busy };

    public static InstallItem Done(UpgradeResult result, string id = "Example.Editor", UpgradeFailure failure = UpgradeFailure.None, string? code = null, bool phantom = false, AppCheck? after = null) =>
        new(Request(id), InstallStage.Done) { Done = new InstallDone(new UpgradeOutcome(result, failure, code), phantom, after) };

    public static UpgradeProgress Downloading(ulong bytes, ulong total) => new(UpgradeStage.Downloading, bytes, total, total == 0 ? 0 : (double)bytes / total, 0);

    // "Example.Editor" becomes "Example Editor".
    public static string Name(string id) => id.Replace('.', ' ');
}

// A stand-in UI thread: posted work runs when the test pumps it.
internal sealed class TestUi
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Post(Action action) => _queue.Enqueue(action);

    public int Pump()
    {
        var ran = 0;
        while (_queue.TryDequeue(out var action))
        {
            action();
            ran++;
        }
        return ran;
    }
}

// Records what the page asks of the install queue.
internal sealed class FakeInstaller : IInstaller
{
    public List<InstallRequest> Enqueued { get; } = [];
    public List<PackageKey> Cancelled { get; } = [];

    public void Enqueue(IEnumerable<InstallRequest> requests) => Enqueued.AddRange(requests);

    public void Cancel(PackageKey package) => Cancelled.Add(package);
}

// A unique folder under %TEMP%, deleted when the test ends.
public sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sut-tests-" + Guid.NewGuid().ToString("N"));

    public string PathOf(string name) => Path.Combine(Root, name);

    // A worker thread may still be writing a last log line, so deleting retries briefly.
    public void Dispose()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }
}
