namespace SoftwareUpdateTracker.Core.Tracking;

public sealed record TrackedApp
{
    public required string Id { get; set; }
    public required string Source { get; set; }
    // Last name winget reported, kept for when the app goes missing.
    public string Name { get; set; } = "";
    public bool Auto { get; set; }
    public string? SkippedVersion { get; set; }
    public Offer? Offer { get; set; }

    // winget ids are case-insensitive.
    public bool Matches(string id, string source) =>
        string.Equals(Id, id, StringComparison.OrdinalIgnoreCase) && string.Equals(Source, source, StringComparison.OrdinalIgnoreCase);
}
