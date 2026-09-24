using SoftwareUpdateTracker.Core.Launch;
using Xunit;

namespace SoftwareUpdateTracker.Core.Tests.Launch;

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
    [InlineData("--cleanup-notifications", true)]
    [InlineData("--startup", false)]
    public void MaintenanceVerbs_RunBeforeElevationCheck(string arg, bool expected) =>
        Assert.Equal(expected, LaunchPolicy.IsMaintenanceVerb([arg]));

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--startup" }, false)]
    public void FlyoutOpensOnLaunch_UnlessStartup(string[] args, bool expected) =>
        Assert.Equal(expected, LaunchPolicy.OpenFlyoutOnLaunch(args));
}
