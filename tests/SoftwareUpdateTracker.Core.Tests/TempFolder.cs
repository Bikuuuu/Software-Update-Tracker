namespace SoftwareUpdateTracker.Core.Tests;

// A unique folder under %TEMP%, deleted when the test ends.
public sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sut-tests-" + Guid.NewGuid().ToString("N"));

    public string PathOf(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
