using TinyTracker.Core.Checking;
using Xunit;

namespace TinyTracker.WinGet.Tests.Integration;

// Real winget, read-only. Skips when winget is missing or too old, unless TINYTRACKER_WINGET_REQUIRED=1.
internal static class RealWinGet
{
    public static async Task<WinGetSession> OpenAsync()
    {
        try
        {
            return await WinGetSession.OpenAsync(TestContext.Current.CancellationToken);
        }
        catch (PackageSourceException e) when (Environment.GetEnvironmentVariable("TINYTRACKER_WINGET_REQUIRED") != "1")
        {
            Assert.Skip($"winget isn't usable here: {e.Problem}");
            throw;
        }
    }
}
