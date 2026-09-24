namespace SoftwareUpdateTracker.Core.Tracking;

// Bookkeeping for the version winget currently offers.
public sealed record Offer
{
    public required string Version { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public DateTimeOffset? LastAutoAttempt { get; init; }
    public bool Phantom { get; init; }
}
