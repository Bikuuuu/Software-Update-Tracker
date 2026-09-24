namespace SoftwareUpdateTracker.Core.Tracking;

public sealed record TrackedApp
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    // Last name winget reported, kept for when the app goes missing.
    public string Name { get; init; } = "";
    public bool Auto { get; init; }
    public string? SkippedVersion { get; init; }
    public Offer? Offer { get; init; }

    // winget ids are case-insensitive.
    public bool Matches(string id, string source) =>
        string.Equals(Id, id, StringComparison.OrdinalIgnoreCase) && string.Equals(Source, source, StringComparison.OrdinalIgnoreCase);
}
