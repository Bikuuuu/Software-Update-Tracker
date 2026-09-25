namespace SoftwareUpdateTracker.Core.Launch;

// Mirrors TOKEN_ELEVATION_TYPE.
public enum ElevationType { Default = 1, Full = 2, Limited = 3 }

public static class LaunchPolicy
{
    public const string StartupFlag = "--startup";
    // Debug builds only: made-up apps instead of winget.
    public const string DemoFlag = "--demo";
    private static readonly string[] MaintenanceVerbs = ["--cleanup-notifications"];

    // Only a split-token (UAC) elevation can be undone via Explorer; with UAC off it would loop.
    public static bool ShouldRelaunchUnelevated(ElevationType type) => type == ElevationType.Full;

    public static bool IsMaintenanceVerb(IReadOnlyList<string> args) => args.Any(MaintenanceVerbs.Contains);

    public static bool OpenFlyoutOnLaunch(IReadOnlyList<string> args) => !args.Contains(StartupFlag);

    public static bool IsDemo(IReadOnlyList<string> args) => args.Contains(DemoFlag);
}
