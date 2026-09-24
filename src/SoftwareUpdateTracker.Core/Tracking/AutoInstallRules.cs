using SoftwareUpdateTracker.Core.Settings;

namespace SoftwareUpdateTracker.Core.Tracking;

// What the PC is doing, as the auto-install rules see it.
public sealed record SystemState(bool FullScreen, bool Metered, bool BatterySaver);

public enum AutoBlock
{
    None,
    AutoOff,
    NotAvailable,
    TooNew,
    FullScreen,
    Metered,
    BatterySaver,
    RecentlyAttempted,
}

public static class AutoInstallRules
{
    public static readonly TimeSpan AttemptCooldown = TimeSpan.FromHours(12);

    // The first rule holding the app back, or None when it may install now.
    public static AutoBlock Check(TrackedApp app, AppStatus status, AppSettings settings, SystemState system, DateTimeOffset now)
    {
        if (!app.Auto) return AutoBlock.AutoOff;
        if (status != AppStatus.Available || app.Offer is not { } offer) return AutoBlock.NotAvailable;
        if (settings.AutoInstallWaitDays > 0 && now - ReleasedAt(offer, now) < TimeSpan.FromDays(settings.AutoInstallWaitDays))
            return AutoBlock.TooNew;
        if (settings.PauseDuringGames && system.FullScreen) return AutoBlock.FullScreen;
        if (system.Metered) return AutoBlock.Metered;
        if (system.BatterySaver) return AutoBlock.BatterySaver;
        // An attempt after now means the clock went back, so it doesn't count.
        if (offer.LastAutoAttempt is { } last && last <= now && now - last < AttemptCooldown) return AutoBlock.RecentlyAttempted;
        return AutoBlock.None;
    }

    // Release date when known and not ahead of now, else the day we first saw the version.
    private static DateTimeOffset ReleasedAt(Offer offer, DateTimeOffset now)
    {
        if (offer.ReleaseDate is not { } date) return offer.FirstSeen;
        var released = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return released <= now ? released : offer.FirstSeen;
    }
}
