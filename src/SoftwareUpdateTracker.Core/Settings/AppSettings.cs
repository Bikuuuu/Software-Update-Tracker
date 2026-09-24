namespace SoftwareUpdateTracker.Core.Settings;

// Defaults follow the spec's Settings table. Start with Windows lives in the Run key, not here.
public sealed record AppSettings
{
    public const int DefaultCheckIntervalHours = 6;
    public const int DefaultSpeedLimitKBps = 17500;

    public static IReadOnlyList<int> CheckIntervalChoices { get; } = [1, 3, 6, 12, 24];
    public static IReadOnlyList<int> WaitDayChoices { get; } = [0, 1, 3, 7];

    public int CheckIntervalHours { get; init; } = DefaultCheckIntervalHours;
    public bool SilentMode { get; init; }
    public int AutoInstallWaitDays { get; init; }
    public bool PauseDuringGames { get; init; } = true;
    public bool SpeedLimitEnabled { get; init; }
    public int SpeedLimitKBps { get; init; } = DefaultSpeedLimitKBps;
    public bool ShowNotifications { get; init; } = true;
    // Null when the user cleared it.
    public Shortcut? OpenShortcut { get; init; } = Shortcut.Default;
    public bool AutoSelfUpdate { get; init; } = true;

    // Out-of-range values from hand edits fall back to defaults.
    public AppSettings Normalize() => this with
    {
        CheckIntervalHours = CheckIntervalChoices.Contains(CheckIntervalHours) ? CheckIntervalHours : DefaultCheckIntervalHours,
        AutoInstallWaitDays = WaitDayChoices.Contains(AutoInstallWaitDays) ? AutoInstallWaitDays : 0,
        SpeedLimitKBps = SpeedLimitKBps > 0 ? SpeedLimitKBps : DefaultSpeedLimitKBps,
        OpenShortcut = OpenShortcut is { Key: < 1 or > 254 } ? Shortcut.Default : OpenShortcut,
    };
}
