using SoftwareUpdateTracker.Presentation.Demo;
using Xunit;

namespace SoftwareUpdateTracker.Presentation.Tests.Demo;

public sealed class DemoFolderTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Create_RemovesFoldersLeftByEarlierDemos_AndCreatesNothing()
    {
        Directory.CreateDirectory(_temp.PathOf("sut-demo-left"));
        Directory.CreateDirectory(_temp.PathOf("unrelated"));
        var folder = DemoFolder.Create(_temp.Root);
        Assert.StartsWith(_temp.PathOf("sut-demo-"), folder);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(["unrelated"], Directory.GetDirectories(_temp.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void Delete_RetriesWhileAFileIsStillOpen()
    {
        var folder = _temp.PathOf("sut-demo-run");
        Directory.CreateDirectory(folder);
        var held = new FileStream(Path.Combine(folder, "app.log"), FileMode.Create, FileAccess.Write, FileShare.None);
        var sleeps = 0;
        Assert.True(DemoFolder.Delete(folder, _ =>
        {
            sleeps++;
            held.Dispose();
        }));
        Assert.Equal(1, sleeps);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Delete_GivesUpQuietly_WhenTheFileStaysOpen()
    {
        var folder = _temp.PathOf("sut-demo-run");
        Directory.CreateDirectory(folder);
        using var held = new FileStream(Path.Combine(folder, "app.log"), FileMode.Create, FileAccess.Write, FileShare.None);
        Assert.False(DemoFolder.Delete(folder, _ => { }));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Delete_OfAMissingFolder_Succeeds() => Assert.True(DemoFolder.Delete(_temp.PathOf("sut-demo-gone")));
}
