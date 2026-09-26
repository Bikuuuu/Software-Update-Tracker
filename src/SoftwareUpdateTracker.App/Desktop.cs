using SoftwareUpdateTracker.Core.Checking;
using SoftwareUpdateTracker.Core.Logging;
using SoftwareUpdateTracker.Presentation.Settings;
using SoftwareUpdateTracker.WinGet;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace SoftwareUpdateTracker.App;

// What the Settings page asks of Windows, on the UI thread.
internal sealed class Desktop(Action<string> openLink, bool demo, FileLog log) : IDesktop
{
    public void OpenLink(string url) => openLink(url);

    public void OpenFolder(string path) => _ = OpenFolderAsync(path);

    // Another app may hold the clipboard for a moment, so setting it gets one more try.
    public bool Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                break;
            }
            catch (Exception e) when (attempt < 2)
            {
                log.Warn($"Clipboard busy: 0x{e.HResult:X8}");
                Thread.Sleep(100);
            }
            catch (Exception e)
            {
                log.Warn($"Clipboard not set: 0x{e.HResult:X8}");
                return false;
            }
        }
        try
        {
            // The text stays on the clipboard after the app quits.
            Clipboard.Flush();
        }
        catch (Exception e)
        {
            log.Warn($"Clipboard not flushed: 0x{e.HResult:X8}");
        }
        return true;
    }

    // The demo asks winget nothing.
    public async Task<string?> WinGetVersionAsync(CancellationToken ct)
    {
        if (demo) return null;
        try
        {
            return await WinGetSession.ReadVersionAsync(ct);
        }
        catch (PackageSourceException)
        {
            return null;
        }
    }

    private async Task OpenFolderAsync(string path)
    {
        try
        {
            if (!await Launcher.LaunchFolderPathAsync(path)) log.Warn("Logs folder not opened");
        }
        catch (Exception e)
        {
            log.Warn($"Logs folder not opened: 0x{e.HResult:X8}");
        }
    }
}
