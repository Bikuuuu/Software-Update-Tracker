using Xunit;

namespace SoftwareUpdateTracker.Core.Tests;

public class AppInfoTests
{
    [Fact]
    public void Name_IsTheProductName() => Assert.Equal("Software Update Tracker", AppInfo.Name);

    [Fact]
    public void InstanceKey_HasNoSpaces() => Assert.Equal("SoftwareUpdateTracker", AppInfo.InstanceKey);
}
