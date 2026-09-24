using System.Security.Principal;

namespace SoftwareUpdateTracker.App.Interop;

internal static class ProcessInfo
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
