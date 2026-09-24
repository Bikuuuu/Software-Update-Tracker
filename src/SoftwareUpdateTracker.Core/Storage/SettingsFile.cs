using SoftwareUpdateTracker.Core.Settings;
using SoftwareUpdateTracker.Core.Tracking;

namespace SoftwareUpdateTracker.Core.Storage;

// settings.json: the settings plus the tracked apps with their bookkeeping.
public sealed record SettingsFile
{
    public int Version { get; init; } = 1;
    public AppSettings Settings { get; init; } = new();
    public IReadOnlyList<TrackedApp> Apps { get; init; } = [];

    public SettingsFile Normalize() => this with
    {
        Settings = Settings.Normalize(),
        Apps = Apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Source))
            .DistinctBy(a => (a.Id.ToUpperInvariant(), a.Source.ToUpperInvariant()))
            .ToList(),
    };
}
