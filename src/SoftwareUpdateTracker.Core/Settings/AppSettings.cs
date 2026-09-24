namespace SoftwareUpdateTracker.Core.Settings;

// Defaults follow the spec's Settings table. Start with Windows lives in the Run key, not here.
public sealed record AppSettings
{
    public const int DefaultCheckIntervalHours = 6;
    public const int DefaultSpeedLimitKBps = 17500;

    public static IReadOnlyList<int> CheckIntervalChoices { get; } = [1, 3, 6, 12, 24];
    public static IReadOnlyList<int> WaitDayChoices { get; } = [0, 1, 3, 7];

    public int CheckIntervalHours { get; set; } = DefaultCheckIntervalHours;
    public bool SilentMode { get; set; }
    public int AutoInstallWaitDays { get; set; }
    public bool PauseDuringGames { get; set; } = true;
    public bool SpeedLimitEnabled { get; set; }
    public int SpeedLimitKBps { get; set; } = DefaultSpeedLimitKBps;
    public bool ShowNotifications { get; set; } = true;
    // Null when the user cleared it.
    public Shortcut? OpenShortcut { get; set; } = Shortcut.Default;
    public bool AutoSelfUpdate { get; set; } = true;

    // Out-of-range values from hand edits fall back to defaults.
    public AppSettings Normalize() => this with
    {
        CheckIntervalHours = CheckIntervalChoices.Contains(CheckIntervalHours) ? CheckIntervalHours : DefaultCheckIntervalHours,
        AutoInstallWaitDays = WaitDayChoices.Contains(AutoInstallWaitDays) ? AutoInstallWaitDays : 0,
        SpeedLimitKBps = SpeedLimitKBps > 0 ? SpeedLimitKBps : DefaultSpeedLimitKBps,
        OpenShortcut = OpenShortcut is { } shortcut && !shortcut.IsValid() ? Shortcut.Default : OpenShortcut,
    };
}
