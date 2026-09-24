# Spike results

Date: 2026-09-25. Windows App SDK 2.5.1. winget 1.29.380 (local and runner after update).

| # | Question | Result | Decision |
|---|---|---|---|
| S1 | Cloaked flyout: acrylic, rounded, placement, dismissal, no flicker | Manual check passed: bottom-right placement, rounded corners, accent progress bar, click-away / Esc / tray-toggle close, no flicker in 10 opens, absent from Alt+Tab. The window rect matched FlyoutPlacement at 100% (2168,1288 380×92). | Keep, with changes: no always-on-top; theme must follow Windows mode |
| S2 | Raw Shell_NotifyIcon tray: add, menu, TaskbarCreated, single instance, elevated relaunch | Icon added; Open/Quit menu works; the icon stays registered after TaskbarCreated; a second launch leaves one process; an elevated launch relaunches unelevated; WM_CLOSE exits cleanly and removes the icon. | Keep raw interop; no third-party tray library |
| S3 | Toasts with buttons, unpackaged self-contained; cleanup | `AppNotificationManager.Register` fails with 0x8007007E on 2.5.1 and 2.2.0 (WindowsAppSDK #6774: the Insights resource DLL ships only in the installed Windows App Runtime) and leaves HKCU keys that `UnregisterAll` can't remove. The Toolkit fallback fails NU1904 (critical CVE in System.Drawing.Common 4.7.0). Direct `Windows.UI.Notifications` with our own `AppUserModelId` key works: name, icon, both buttons; clicking View opens the flyout. Cleanup leaves zero registry changes. | Change: direct `Windows.UI.Notifications` |
| S4 | COM list, metadata, upgrade progress, cancel | Local: 129 correlated, 10 with updates, 5.8 s; scope, elevation requirement and release notes URL readable. Runner: Notepad++ 8.9.7 → 8.9.8.1 Ok, states Downloading → Installing (install 0 → 1) → PostInstall. VLC cancel during download → Cancelled. | Keep COM |
| S5 | COM from an elevated process | The runner runs at High integrity; list, upgrade and cancel all worked elevated. Administrator Protection is untested (unavailable). | Keep; the unelevated fallback stays |
| S6 | CLI --proxy through the relay caps downloads | Relay tests: 200 KB at 100 KB/s took 1.6–3.5 s. Runner: a 2000 KB/s limit measured 1982 KB/s over 60.8 MB, exit 0. | Keep |
| S7 | Idle CPU, RAM and GPU vs budget | Without trimming: 65.4 MB private working set. With EcoQoS + trim: 10-minute idle CPU 0.006%, 8.5 MB, GPU 0% → PASS. | Keep |
| S8 | winget on windows-2025 | Preinstalled v1.11.510 is too old: ComInterop fails on `PackageManager.Version` (IPackageManager7 not registered). `Repair-WinGetPackageManager -Latest` updates it to v1.29.380. 7-Zip is preinstalled at the latest version, so upgrade tests need another package. | Update winget in every workflow that uses it |

## Notes

- **S1, always-on-top:** WinUI 3 windows refuse `WS_EX_TOPMOST` on SDK 2.5.1 and 2.2.0. That holds for `OverlappedPresenter.IsAlwaysOnTop` and for `SetWindowPos(HWND_TOPMOST)` from inside or outside the process. It also holds with the presenter configuration, backdrop, `IsShownInSwitchers` and cloaking all removed. A plain Win32 window in the same process accepts it. Related: microsoft-ui-xaml#9990. The flyout relies on foreground activation instead: a tray click, the hotkey and a Start-menu launch all grant it.
- **S1, theme:** the flyout follows the app mode (light on the test PC). Spec §4.2 requires the Windows mode, so the theme must be set from `SystemUsesLightTheme` and tracked.
- **S4:** the out-of-proc COM vectors do not expose `IIterable`. `foreach`/LINQ over `Matches` throws `E_NOINTERFACE`, so always index (`Count` + `[i]`). `GetApplicableInstaller()` reports the scope for a fresh install; update routing must use the installed scope.
- **S1, multi-monitor:** the flyout now moves onto the target monitor before sizing, so a DPI change can't rescale it. A real check with the taskbar on a 125/150% monitor (and one left of the primary) is still owed; Plan 4 must run it.
- **S1, launch:** the flyout opens on every launch except `--startup`. The Explorer relaunch used to drop elevation can't forward arguments; if later verbs need them, Plan 4 must use shell dispatch instead. Maintenance verbs run before the elevation check so the elevated uninstaller can clean up.
- **S3, clicks after a restart:** clicks work only while the process that showed the toast is running (no COM activator). The app clears its toasts on quit so none go stale. Plan 6 decides whether to register a COM activator (one more HKCU key to clean up).
- **S7, background work:** Efficiency mode is tied to the flyout being hidden. Later plans must leave idle mode while checks, the relay or installs run.
- **S6, relay:** reads are capped at a tenth of a second of data, and waits run in ≤100 ms slices, so limit changes apply within about 100 ms.
- **S8 / §7:** an old winget throws `InvalidCastException` on `PackageManager.Version`. The app must treat that as "winget needs an update".
- **Tooling:**
  - Under the .NET 10 SDK, xunit.v3 needs `dotnet test` in Microsoft.Testing.Platform mode (`global.json` `test.runner`).
  - Referencing `Microsoft.Windows.CsWinRT` directly breaks the build without a full Windows SDK. The ComInterop package's prebuilt projection is enough.
  - `workflow_dispatch` only works once a workflow is on the default branch.
