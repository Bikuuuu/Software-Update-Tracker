using System.Runtime.InteropServices;
using SoftwareUpdateTracker.Core.Launch;

namespace SoftwareUpdateTracker.App.Interop;

internal static class ProcessInfo
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevationTypeClass = 18;

    public static ElevationType GetElevationType()
    {
        if (!OpenProcessToken(NativeMethods.GetCurrentProcess(), TOKEN_QUERY, out var token)) return ElevationType.Default;
        try
        {
            return GetTokenInformation(token, TokenElevationTypeClass, out var type, sizeof(int), out _)
                ? (ElevationType)type
                : ElevationType.Default;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returned);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
