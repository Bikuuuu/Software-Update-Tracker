using TinyTracker.Core.Launch;
using Xunit;

namespace TinyTracker.Core.Tests.Launch;

public class LaunchPolicyTests
{
    [Fact]
    public void SplitTokenElevated_Relaunches() =>
        Assert.True(LaunchPolicy.ShouldRelaunchUnelevated(ElevationType.Full));

    [Fact]
    public void UacOffOrBuiltInAdmin_RunsInPlaceInsteadOfLooping() =>
        Assert.False(LaunchPolicy.ShouldRelaunchUnelevated(ElevationType.Default));

    [Fact]
    public void NormalUser_RunsInPlace() =>
        Assert.False(LaunchPolicy.ShouldRelaunchUnelevated(ElevationType.Limited));

    [Theory]
    [InlineData("--cleanup", true)]
    [InlineData("--cleanup-notifications", true)]
    [InlineData("--startup", false)]
    public void MaintenanceVerbs_RunBeforeElevationCheck(string arg, bool expected) =>
        Assert.Equal(expected, LaunchPolicy.IsMaintenanceVerb([arg]));

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--startup" }, false)]
    public void FlyoutOpensOnLaunch_UnlessStartup(string[] args, bool expected) =>
        Assert.Equal(expected, LaunchPolicy.OpenFlyoutOnLaunch(args));

    [Theory]
    [InlineData(new[] { "--demo" }, true)]
    [InlineData(new[] { "--startup", "--demo" }, true)]
    [InlineData(new[] { "--startup" }, false)]
    [InlineData(new[] { "demo" }, false)]
    public void Demo_NeedsTheDemoFlag(string[] args, bool expected) =>
        Assert.Equal(expected, LaunchPolicy.IsDemo(args));
}
