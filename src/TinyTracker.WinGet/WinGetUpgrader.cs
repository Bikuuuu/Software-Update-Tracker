using TinyTracker.Core.Checking;
using TinyTracker.Core.Installing;
using TinyTracker.Core.Tracking;

namespace TinyTracker.WinGet;

// Upgrades one package through winget's COM API, in a fresh session.
public sealed class WinGetUpgrader : IPackageUpgrader
{
    public async Task<UpgradeOutcome> UpgradeAsync(PackageKey package, string version, IProgress<UpgradeProgress>? progress, CancellationToken ct)
    {
        if (!string.Equals(package.Source, WinGetSession.SourceName, StringComparison.OrdinalIgnoreCase)) return new UpgradeOutcome(UpgradeResult.NotInstalled);
        try
        {
            var session = await WinGetSession.OpenAsync(ct);
            return await Task.Run(() => session.UpgradeAsync(package.Id, version, progress, ct), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return new UpgradeOutcome(UpgradeResult.Cancelled);
        }
        catch (PackageSourceException e)
        {
            return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.WinGetUnavailable, e.Code);
        }
        // IPackageUpgrader never throws.
        catch (Exception e)
        {
            return new UpgradeOutcome(UpgradeResult.Failed, UpgradeFailure.WinGetUnavailable, $"0x{e.HResult:X8}");
        }
    }
}
