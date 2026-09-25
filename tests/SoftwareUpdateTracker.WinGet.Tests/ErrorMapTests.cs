using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;
using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.Installing;
using Xunit;

namespace SoftwareUpdateTracker.WinGet.Tests;

public class ErrorMapTests
{
    private static UpgradeOutcome Map(InstallResultStatus status, uint error = 0, uint installer = 0) =>
        ErrorMap.ForUpgrade(status, unchecked((int)error), installer);

    [Fact]
    public void Ok_IsUpdated() => Assert.Equal(new UpgradeOutcome(UpgradeResult.Updated), Map(InstallResultStatus.Ok));

    [Theory]
    [InlineData(InstallResultStatus.Ok, 0u, 3010u)]
    [InlineData(InstallResultStatus.InstallError, 0x8A15010Bu, 1641u)]
    [InlineData(InstallResultStatus.InstallError, 0x8A150109u, 0u)]
    [InlineData(InstallResultStatus.InstallError, 0x8A15010Au, 8u)]
    [InlineData(InstallResultStatus.InstallError, 0x8A150006u, 3010u)]
    public void RebootCodes_MeanRestartNeeded(InstallResultStatus status, uint error, uint installer) =>
        Assert.Equal(UpgradeResult.RestartNeeded, Map(status, error, installer).Result);

    [Theory]
    [InlineData(0x8A150101u)]
    [InlineData(0x8A150103u)]
    [InlineData(0x8A150111u)]
    public void InUseCodes_MeanAppInUse(uint error) =>
        Assert.Equal(UpgradeResult.AppInUse, Map(InstallResultStatus.InstallError, error).Result);

    [Theory]
    [InlineData(0x8A150102u, 0u)]
    [InlineData(0x8A150049u, 1618u)]
    public void AnotherInstallRunning_IsBusy(uint error, uint installer) =>
        Assert.Equal(UpgradeResult.Busy, Map(InstallResultStatus.InstallError, error, installer).Result);

    [Fact]
    public void RequiresAdmin_NeedsAdmin() =>
        Assert.Equal(UpgradeResult.NeedsAdmin, Map(InstallResultStatus.InstallError, 0x8A150019).Result);

    [Theory]
    [InlineData(0x800704C7u, 0u)]
    [InlineData(0x8A150006u, 1223u)]
    public void DeclinedPrompt_IsPermissionDeclined(uint error, uint installer) =>
        Assert.Equal(UpgradeResult.PermissionDeclined, Map(InstallResultStatus.InstallError, error, installer).Result);

    [Theory]
    [InlineData(InstallResultStatus.NoApplicableUpgrade, 0x8A15002Bu)]
    [InlineData(InstallResultStatus.InstallError, 0x8A15004Fu)]
    [InlineData(InstallResultStatus.NoApplicableUpgrade, 0u)]
    public void NothingNewer_IsNoUpdate(InstallResultStatus status, uint error) =>
        Assert.Equal(UpgradeResult.NoUpdate, Map(status, error).Result);

    [Theory]
    [InlineData(InstallResultStatus.InstallError, 0x8A150105u, UpgradeFailure.DiskFull)]
    [InlineData(InstallResultStatus.InstallError, 0x8A150106u, UpgradeFailure.NotEnoughMemory)]
    [InlineData(InstallResultStatus.InstallError, 0x8A150107u, UpgradeFailure.NoNetwork)]
    [InlineData(InstallResultStatus.InstallError, 0x8A15010Fu, UpgradeFailure.BlockedByPolicy)]
    [InlineData(InstallResultStatus.BlockedByPolicy, 0x8A15003Au, UpgradeFailure.BlockedByPolicy)]
    [InlineData(InstallResultStatus.InstallError, 0x8A150104u, UpgradeFailure.MissingDependency)]
    [InlineData(InstallResultStatus.InstallError, 0x8A150113u, UpgradeFailure.NotSupported)]
    [InlineData(InstallResultStatus.InstallError, 0x8A15010Eu, UpgradeFailure.NewerInstalled)]
    [InlineData(InstallResultStatus.InstallError, 0x8A15010Cu, UpgradeFailure.InstallerCancelled)]
    [InlineData(InstallResultStatus.DownloadError, 0x8A150011u, UpgradeFailure.HashMismatch)]
    [InlineData(InstallResultStatus.DownloadError, 0x8A150008u, UpgradeFailure.DownloadFailed)]
    [InlineData(InstallResultStatus.NoApplicableInstallers, 0x8A150010u, UpgradeFailure.NoApplicableInstaller)]
    public void KnownFailures_KeepTheirReasonAndCode(InstallResultStatus status, uint error, UpgradeFailure failure)
    {
        var outcome = Map(status, error);
        Assert.Equal(UpgradeResult.Failed, outcome.Result);
        Assert.Equal(failure, outcome.Failure);
        Assert.Equal($"0x{error:X8}", outcome.Code);
    }

    [Theory]
    [InlineData(InstallResultStatus.DownloadError, 0u, 0u, UpgradeFailure.DownloadFailed)]
    [InlineData(InstallResultStatus.BlockedByPolicy, 0u, 0u, UpgradeFailure.BlockedByPolicy)]
    [InlineData(InstallResultStatus.CatalogError, 0u, 0u, UpgradeFailure.WinGetUnavailable)]
    [InlineData(InstallResultStatus.InstallError, 0x8A150006u, 1603u, UpgradeFailure.InstallerFailed)]
    [InlineData(InstallResultStatus.ManifestError, 0u, 0u, UpgradeFailure.Other)]
    public void OtherFailures_FallBackOnTheStatus(InstallResultStatus status, uint error, uint installer, UpgradeFailure failure) =>
        Assert.Equal(failure, Map(status, error, installer).Failure);

    [Theory]
    [InlineData(0x8A150006u, 1603u, "0x8A150006, installer 1603")]
    [InlineData(0x8A150011u, 0u, "0x8A150011")]
    [InlineData(0u, 3010u, "installer 3010")]
    [InlineData(0u, 0u, null)]
    public void Code_ShowsTheHResultAndTheInstallerCode(uint error, uint installer, string? code) =>
        Assert.Equal(code, ErrorMap.Code(unchecked((int)error), installer));

    [Theory]
    [InlineData(0x80040154u, CheckProblem.WinGetMissing)]
    [InlineData(0x800706BAu, CheckProblem.WinGetUnreachable)]
    [InlineData(0x800706BEu, CheckProblem.WinGetUnreachable)]
    [InlineData(0x80010108u, CheckProblem.WinGetUnreachable)]
    [InlineData(0x80004005u, CheckProblem.Failed)]
    public void ComFailures_MapToProblems(uint hresult, CheckProblem problem) =>
        Assert.Equal(problem, ErrorMap.ForException(new COMException("winget", unchecked((int)hresult))));

    [Fact]
    public void MissingInterface_MeansWinGetIsTooOld() =>
        Assert.Equal(CheckProblem.WinGetTooOld, ErrorMap.ForException(new InvalidCastException()));
}
