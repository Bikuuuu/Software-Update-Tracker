using TinyTracker.Core.Settings;
using TinyTracker.Core.Tracking;

namespace TinyTracker.Core.Storage;

// settings.json: the settings plus the tracked apps with their bookkeeping.
public sealed record SettingsFile
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public IReadOnlyList<TrackedApp> Apps { get; set; } = [];

    public SettingsFile Normalize() => this with
    {
        Settings = Settings.Normalize(),
        Apps = Apps
            .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Source))
            .DistinctBy(a => (a.Id.ToUpperInvariant(), a.Source.ToUpperInvariant()))
            .ToList(),
    };
}
