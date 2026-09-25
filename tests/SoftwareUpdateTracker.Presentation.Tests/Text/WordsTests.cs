using SoftwareUpdateTracker.Core.Installing;
using SoftwareUpdateTracker.Core.Inventory;
using SoftwareUpdateTracker.Presentation.Text;
using Xunit;

namespace SoftwareUpdateTracker.Presentation.Tests.Text;

public class WordsTests
{
    private const ulong KB = 1024, MB = KB * 1024, GB = MB * 1024;
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0UL, "0 KB")]
    [InlineData(512UL, "0.5 KB")]
    [InlineData(850 * KB, "850 KB")]
    [InlineData(3 * MB / 2, "1.5 MB")]
    [InlineData(180 * MB, "180 MB")]
    [InlineData(6 * GB / 5, "1.2 GB")]
    public void Size_UsesTheLargestFittingUnit(ulong bytes, string text) => Assert.Equal(text, Words.Size(bytes));

    [Theory]
    [InlineData(180 * MB, 400 * MB, "180 of 400 MB")]
    [InlineData(GB / 2, 3 * GB / 2, "0.5 of 1.5 GB")]
    [InlineData(180 * MB, 0UL, "180 MB")]
    public void Downloaded_ShowsBothInTheTotalsUnit(ulong done, ulong total, string text) => Assert.Equal(text, Words.Downloaded(done, total));

    [Fact]
    public void Speed_IsPerSecond() => Assert.Equal(["20 MB/s", "850 KB/s", "0 KB/s"], [Words.Speed(20 * MB), Words.Speed(850 * KB), Words.Speed(-1)]);

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(-300, "just now")]
    [InlineData(120, "2 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(3 * 3600 + 59 * 60, "3 h ago")]
    [InlineData(26 * 3600, "1 day ago")]
    [InlineData(3 * 86400, "3 days ago")]
    public void Ago_RoundsDown(int seconds, string text) => Assert.Equal(text, Words.Ago(Now - TimeSpan.FromSeconds(seconds), Now));

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(-2, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(5, "5 days ago")]
    [InlineData(30, "30 days ago")]
    [InlineData(45, "Aug 11, 2026")]
    public void Released_CountsDays(int daysAgo, string text) => Assert.Equal(text, Words.Released(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-daysAgo), Now));

    [Theory]
    [InlineData(-60, "less than a minute")]
    [InlineData(59, "less than a minute")]
    [InlineData(60, "1 min")]
    [InlineData(42 * 60 - 30, "42 min")]
    [InlineData(5 * 3600 + 57 * 60 + 30, "5 h 58 min")]
    [InlineData(6 * 3600, "6 h")]
    public void Until_RoundsUpToTheMinute(int seconds, string text) => Assert.Equal(text, Words.Until(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Reason_WordsAKnownName_AndFallsBackForOthers() =>
        Assert.Equal(["Not enough disk space", "The update failed", ""], [Words.Reason("DiskFull"), Words.Reason("SomethingNew"), Words.Reason(null)]);

    // History keeps these names, so each needs words.
    public static TheoryData<string> ReasonNames()
    {
        var names = new TheoryData<string>("Phantom");
        foreach (var failure in Enum.GetValues<UpgradeFailure>().Where(f => f != UpgradeFailure.None)) names.Add(failure.ToString());
        foreach (var result in Enum.GetValues<UpgradeResult>().Where(r => r is not (UpgradeResult.Updated or UpgradeResult.Cancelled or UpgradeResult.Failed))) names.Add(result.ToString());
        return names;
    }

    [Theory]
    [MemberData(nameof(ReasonNames))]
    public void EveryReason_HasItsOwnWords(string name) => Assert.NotNull(Strings.ResourceManager.GetString("Reason_" + name, Strings.Culture));

    [Theory]
    [InlineData(UpdatedBy.Steam, "Updated by Steam")]
    [InlineData(UpdatedBy.MicrosoftStore, "Updated by Microsoft Store")]
    [InlineData(UpdatedBy.WindowsUpdate, "Updated by Windows Update")]
    [InlineData(UpdatedBy.DriverTool, "Updated by its driver tool")]
    [InlineData(UpdatedBy.ItSelf, "Updates itself")]
    [InlineData(UpdatedBy.Unknown, "Not in winget")]
    public void Elsewhere_NamesWhatUpdatesTheApp(UpdatedBy by, string text) => Assert.Equal(text, Words.Elsewhere(by));

    [Fact]
    public void Counts_UseOneAndMany() =>
        Assert.Equal(
            ["1 update ready", "3 updates ready", "1 app is up to date", "9 apps are up to date", "1 app tracked", "0 apps tracked"],
            [Words.UpdatesReady(1), Words.UpdatesReady(3), Words.UpToDate(1), Words.UpToDate(9), Words.AppsTracked(1), Words.AppsTracked(0)]);
}
