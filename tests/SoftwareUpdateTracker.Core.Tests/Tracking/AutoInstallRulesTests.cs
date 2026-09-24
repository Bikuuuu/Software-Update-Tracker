using SoftwareUpdateTracker.Core.Settings;
using SoftwareUpdateTracker.Core.Tracking;
using Xunit;

namespace SoftwareUpdateTracker.Core.Tests.Tracking;

public class AutoInstallRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly SystemState Calm = new(FullScreen: false, Metered: false, BatterySaver: false);
    private static readonly TrackedApp AutoApp = new()
    {
        Id = "Notepad++.Notepad++",
        Source = "winget",
        Auto = true,
        Offer = new Offer { Version = "8.9.8", FirstSeen = Now },
    };

    private static AutoBlock Check(TrackedApp? app = null, AppStatus status = AppStatus.Available, AppSettings? settings = null, SystemState? system = null) =>
        AutoInstallRules.Check(app ?? AutoApp, status, settings ?? new AppSettings(), system ?? Calm, Now);

    [Fact]
    public void AllClear_Installs() => Assert.Equal(AutoBlock.None, Check());

    [Fact]
    public void AutoOff_IsHeldBack() => Assert.Equal(AutoBlock.AutoOff, Check(AutoApp with { Auto = false }));

    [Theory]
    [InlineData(AppStatus.UpToDate)]
    [InlineData(AppStatus.Skipped)]
    [InlineData(AppStatus.Phantom)]
    [InlineData(AppStatus.VersionUnknown)]
    [InlineData(AppStatus.NotFound)]
    public void OnlyAvailableRows_Install(AppStatus status) => Assert.Equal(AutoBlock.NotAvailable, Check(status: status));

    [Fact]
    public void WithinTheWait_IsTooNew()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-2) } };
        Assert.Equal(AutoBlock.TooNew, Check(app, settings: new AppSettings { AutoInstallWaitDays = 3 }));
    }

    [Fact]
    public void AfterTheWait_Installs()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-3) } };
        Assert.Equal(AutoBlock.None, Check(app, settings: new AppSettings { AutoInstallWaitDays = 3 }));
    }

    [Fact]
    public void ReleaseDate_WinsOverFirstSeen()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { ReleaseDate = new DateOnly(2026, 9, 15) } };
        Assert.Equal(AutoBlock.None, Check(app, settings: new AppSettings { AutoInstallWaitDays = 7 }));
    }

    [Fact]
    public void RecentReleaseDate_IsTooNewEvenIfSeenLongAgo()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { FirstSeen = Now.AddDays(-30), ReleaseDate = new DateOnly(2026, 9, 24) } };
        Assert.Equal(AutoBlock.TooNew, Check(app, settings: new AppSettings { AutoInstallWaitDays = 3 }));
    }

    [Fact]
    public void NoWait_IgnoresAFutureReleaseDate()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { ReleaseDate = new DateOnly(2026, 12, 1) } };
        Assert.Equal(AutoBlock.None, Check(app));
    }

    [Fact]
    public void FullScreen_HoldsBackWhenPausingForGames() =>
        Assert.Equal(AutoBlock.FullScreen, Check(system: Calm with { FullScreen = true }));

    [Fact]
    public void FullScreen_IsIgnoredWhenNotPausingForGames() =>
        Assert.Equal(AutoBlock.None, Check(settings: new AppSettings { PauseDuringGames = false }, system: Calm with { FullScreen = true }));

    [Fact]
    public void Metered_IsHeldBack() => Assert.Equal(AutoBlock.Metered, Check(system: Calm with { Metered = true }));

    [Fact]
    public void BatterySaver_IsHeldBack() => Assert.Equal(AutoBlock.BatterySaver, Check(system: Calm with { BatterySaver = true }));

    [Fact]
    public void AttemptWithin12Hours_IsHeldBack()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { LastAutoAttempt = Now.AddHours(-11) } };
        Assert.Equal(AutoBlock.RecentlyAttempted, Check(app));
    }

    [Fact]
    public void Attempt12HoursAgo_TriesAgain()
    {
        var app = AutoApp with { Offer = AutoApp.Offer! with { LastAutoAttempt = Now.AddHours(-12) } };
        Assert.Equal(AutoBlock.None, Check(app));
    }
}
