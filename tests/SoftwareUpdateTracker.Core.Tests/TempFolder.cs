namespace SoftwareUpdateTracker.Core.Tests;

// A unique folder under %TEMP%, deleted when the test ends.
public sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tinytracker-tests-" + Guid.NewGuid().ToString("N"));

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
