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
    public required DateTimeOffset Time { get; init; }
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Name { get; init; }
    public required HistoryResult Result { get; init; }
    public string? FromVersion { get; init; }
    public string? ToVersion { get; init; }
    // Plain words, and the technical code behind Details.
    public string? Reason { get; init; }
    public string? Code { get; init; }
}
