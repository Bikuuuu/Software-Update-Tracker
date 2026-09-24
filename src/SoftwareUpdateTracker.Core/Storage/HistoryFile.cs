using SoftwareUpdateTracker.Core.History;

namespace SoftwareUpdateTracker.Core.Storage;

internal sealed record HistoryFile
{
    public int Version { get; set; } = 1;
    public IReadOnlyList<HistoryEntry> Entries { get; set; } = [];
}
