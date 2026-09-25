namespace SoftwareUpdateTracker.Core.Tracking;

// One installed package as a check saw it. AvailableVersion is null when no update is offered.
public sealed record PackageSnapshot(
    string Id,
    string Source,
    string Name,
    string InstalledVersion,
    string? AvailableVersion,
    string Publisher = "",
    string? ReleaseNotesUrl = null);
