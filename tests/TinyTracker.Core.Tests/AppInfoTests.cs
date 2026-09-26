using Xunit;

namespace TinyTracker.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Name_IsTheProductName() => Assert.Equal("Tiny Tracker", AppInfo.Name);

    [Fact]
    public void InstanceKey_HasNoSpaces() => Assert.Equal("TinyTracker", AppInfo.InstanceKey);

    [Fact]
    public void RepositoryUrl_IsTheRenamedRepo() => Assert.Equal("https://github.com/Bikuuuu/Tiny-Tracker-Software-Update-Tracker", AppInfo.RepositoryUrl);
}
