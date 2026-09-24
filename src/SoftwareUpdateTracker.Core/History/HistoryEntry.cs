namespace SoftwareUpdateTracker.Core.History;

public enum HistoryResult
{
    Updated,
    Failed,
    Skipped,
    Cancelled,
}

public sealed record HistoryEntry
{
    public required DateTimeOffset Time { get; set; }
    public required string Id { get; set; }
    public required string Source { get; set; }
    public required string Name { get; set; }
    public required HistoryResult Result { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    // Plain words, and the technical code behind Details.
    public string? Reason { get; set; }
    public string? Code { get; set; }
}
