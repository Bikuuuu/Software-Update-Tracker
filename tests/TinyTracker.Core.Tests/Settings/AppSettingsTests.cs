using TinyTracker.Core.Settings;
using Xunit;

namespace TinyTracker.Core.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var settings = new AppSettings();
        Assert.Equal(6, settings.CheckIntervalHours);
        Assert.False(settings.SilentMode);
        Assert.Equal(0, settings.AutoInstallWaitDays);
        Assert.True(settings.PauseDuringGames);
        Assert.False(settings.SpeedLimitEnabled);
        Assert.Equal(17500, settings.SpeedLimitKBps);
        Assert.True(settings.ShowNotifications);
        Assert.Equal(new Shortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x55), settings.OpenShortcut);
        Assert.True(settings.AutoSelfUpdate);
    }

    [Theory]
    [InlineData(5, 6)]
    [InlineData(0, 6)]
    [InlineData(-1, 6)]
    [InlineData(12, 12)]
    public void Normalize_KeepsOnlyOfferedIntervals(int hours, int expected) =>
        Assert.Equal(expected, new AppSettings { CheckIntervalHours = hours }.Normalize().CheckIntervalHours);

    [Theory]
    [InlineData(2, 0)]
    [InlineData(30, 0)]
    [InlineData(3, 3)]
    public void Normalize_KeepsOnlyOfferedWaits(int days, int expected) =>
        Assert.Equal(expected, new AppSettings { AutoInstallWaitDays = days }.Normalize().AutoInstallWaitDays);

    [Theory]
    [InlineData(0, 17500)]
    [InlineData(-5, 17500)]
    [InlineData(1, 1)]
    [InlineData(250000, 250000)]
    public void Normalize_NeedsAPositiveSpeedLimit(int kbps, int expected) =>
        Assert.Equal(expected, new AppSettings { SpeedLimitKBps = kbps }.Normalize().SpeedLimitKBps);

    [Fact]
    public void Normalize_KeepsAClearedShortcut() =>
        Assert.Null(new AppSettings { OpenShortcut = null }.Normalize().OpenShortcut);

    [Theory]
    [InlineData(ShortcutModifiers.None)]
    [InlineData((ShortcutModifiers)64)]
    [InlineData(ShortcutModifiers.Control | (ShortcutModifiers)64)]
    public void Normalize_ReplacesAShortcutWithoutKnownModifiers(ShortcutModifiers modifiers) =>
        Assert.Equal(Shortcut.Default, new AppSettings { OpenShortcut = new Shortcut(modifiers, 0x55) }.Normalize().OpenShortcut);

    [Fact]
    public void Normalize_KeepsAValidShortcut()
    {
        var shortcut = new Shortcut(ShortcutModifiers.Shift | ShortcutModifiers.Windows, 0x7B);
        Assert.Equal(shortcut, new AppSettings { OpenShortcut = shortcut }.Normalize().OpenShortcut);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void Normalize_ReplacesAnInvalidKeyWithTheDefault(int key) =>
        Assert.Equal(Shortcut.Default, new AppSettings { OpenShortcut = new Shortcut(ShortcutModifiers.Alt, key) }.Normalize().OpenShortcut);
}
