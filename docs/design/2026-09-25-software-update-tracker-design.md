# Software Update Tracker: Design

Status: approved in review, 2026-09-25
Replaces: the PowerShell prototype (`UpdateTracker.ps1`), which stays in git history.

## 1. Summary

Software Update Tracker is a Windows 11 tray app that keeps the apps you choose up to date, using winget. It has no main window. Everything happens in a Windows 11-style flyout that opens from the notification area (including the hidden-icons menu) or from a keyboard shortcut. It checks on a schedule and shows live progress. It installs updates on request, or by itself for apps where you turn on Auto.

## 2. Goals and non-goals

**Goals**
- A native Windows 11 look that feels "modern, live, dynamic": Fluent controls, acrylic, the system light/dark mode and accent color, smooth motion, live progress.
- It only updates the apps the user chose. It never installs new software, uninstalls anything, or changes app settings.
- It leaves nothing behind. See §10.
- Nearly zero cost while idle. See §9.
- Safe admin handling. The UI never runs elevated.
- Public, MIT-licensed, and buildable from source by anyone.

**Non-goals for v1**
- Microsoft Store distribution: the Store blocks the elevated helper that silent mode needs.
- Windows 10.
- ARM64 builds. They come later, once someone can test them.
- Package managers other than winget.
- Apps that are not in winget's catalog.
- Telemetry or analytics of any kind.
- Languages other than English. Strings live in resource files so translations can be added later.

## 3. Decisions

| Topic | Decision |
|---|---|
| Audience | Public GitHub repo, MIT license. Public |
| Name | "Software Update Tracker" everywhere |
| OS | Windows 11 only |
| Stack | C#, .NET 10 (LTS), WinUI 3 (Windows App SDK, pinned version), unpackaged, self-contained, x64 |
| Distribution | Inno Setup 7.1 per-machine installer into Program Files, published on GitHub Releases. No Store |
| Signing | Unsigned at first. Apply to SignPath Foundation after 1.0. If declined, stay unsigned |
| First run | All apps are listed and **none is pre-selected**. The user picks, and can change the list later in Settings |
| Admin rights | Default: ask once per batch. Opt-in "Install updates without asking" (silent mode) |
| Open apps | Try the update first. If the app is in use, offer **Close & update** (close, update, reopen). Never close an app without the user's OK |
| Look | Match Windows: system mode and accent color, acrylic, Segoe UI Variable, Segoe Fluent Icons |
| Icon | Original vector hamster (pink knitted hood, gold heart sunglasses). Detailed art at 32 px and up, simplified art for 16–24 px |
| Extras in v1 | Skip version, History, What's new link, wait N days before auto-install, cancel, self-update, pause during games, global shortcut, download speed limit |
| Testing | Automated on GitHub-hosted runners, plus a manual checklist on a real PC. No Hyper-V, Sandbox or VMs on the test PC, because some anti-cheat software conflicts with them |

## 4. User experience

Mockup: [`flyout-mockup.png`](flyout-mockup.png). Letter tiles stand in for real app icons.

### 4.1 Tray icon
- It shows the simplified hamster at 16/20/24 px and the detailed art at 32 px and up. Assets are pre-rendered from the SVG sources in `assets/icon/`.
- **States:**
  - Idle: plain icon.
  - Updates ready: a small white dot with a dark outline in the corner, visible on dark, light and accent taskbars.
  - Working: a subtle frame animation that runs only while a check or install is active.
- **Tooltip:** "Software Update Tracker: 3 updates ready", "…: Installing NVIDIA App (45%)", or "…: Up to date".
- **Left-click** toggles the flyout. **Right-click** opens a menu: Open, Check now, Update all, Settings, Quit.

### 4.2 Flyout window
- 380 px wide. Its height fits the content, up to 70% of the work area; beyond that the list scrolls.
- It sits bottom-right above the taskbar, 12 px from the edges, on the monitor that holds the taskbar. It is DPI-aware on every monitor.
- **Appearance:** acrylic backdrop, rounded corners, Windows 11 flyout border and shadow. It follows the system (Windows) mode and the accent color live.
- **Opening:** it slides up and fades in, like the system flyouts. It opens instantly because the window is created at startup and hidden with DWM cloaking, never re-created.
- **Closing:** clicking outside (focus loss) or pressing Esc. Clicking the tray icon while the flyout is open closes it; a debounce prevents the close-then-reopen bug.
- It has no taskbar button and doesn't appear in Alt+Tab.
- Pages slide in with a back arrow: **Updates** (home), **Choose apps**, **Settings**, **History**.
- When opened, it shows cached results at once. If the last check is more than 15 minutes old, it refreshes live and the refresh icon spins.

### 4.3 Updates page (home)
- **Header:** hamster icon, app name, and icon buttons for Check now, History and Settings. Below them, a summary: "3 updates ready · checked 2 min ago".
- **Updates** section: one card per app, sorted with in-progress first, then needs-attention, then available. Each card shows:
  - the real app icon, kept in memory only
  - the name, with an **Auto** pill if Auto is on
  - old → new version, with the changed part of the new version in the accent color
  - a status line and an action
- **Row states:**

| State | Status line | Action |
|---|---|---|
| Available | "2 days ago · What's new" | **Update**, plus the "…" menu |
| Waiting | "Waiting…" | ✕ Cancel |
| Downloading | progress bar and "180 of 400 MB · 20 MB/s" (or "180 MB · 20 MB/s" if the size is unknown) | percent and ✕ Cancel |
| Installing | progress bar (indeterminate if unknown) and "Installing…" | none |
| App in use | "G HUB is running" | **Close & update** |
| Needs permission | "Needs admin permission" | **Install** (one prompt for the batch) |
| Failed | the reason in plain words | **Retry**, and **Details** (technical code) |
| Restart needed | "Restart to finish" | none |
| Updated | "Updated to 2025.1.3" | none; the row animates into "Up to date" |
| Phantom | "Installed, but Windows still reports the old version" | **Update anyway** |
| Version unknown | "Version unknown · this app updates itself" | none |
| Not found | "Not installed anymore" / "Not found in winget" | **Stop tracking** |

- **The "…" menu:** Update now, Skip this version (Undo is in the same menu), Auto-update on/off, What's new, Stop tracking. Stop tracking shows an inline "Removed · Undo" for 5 s.
- **Up to date:** a collapsed card ("9 apps are up to date") showing a stack of icons. It expands to the full list and is expanded by default when nothing needs updating.
- **Footer:** "Next check in 5 h 58 min" and the accent button **Update all (N)**. N counts rows in the Available or Failed state; skipped, phantom and unknown-version apps are excluded.
- "What's new" opens the package's release notes URL in the default browser. It is hidden when the package has none.
- Mockup values are examples only; the defaults are in §4.5.
- **Empty state** (no apps chosen): the hamster, "Choose the apps you want to keep up to date", and **Choose apps**.
- **Motion:** list add/remove/reorder transitions, smooth progress, crossfading summary text. All motion stops when the flyout is hidden.

### 4.4 Choose apps page
- Opens automatically on first run, and from Settings.
- It lists every installed app that winget can update, sorted by name, with icon, version and publisher. **Nothing is ticked on first run.**
- A search box filters as you type. There's a "Show selected" filter and a "3 of 48 selected" count.
- Changes apply as you tick. **Done** returns to Updates, and a check runs for newly added apps.
- At the end of the list, a section named **Updated elsewhere (N)** holds the installed apps that winget can't update. These include games from Steam and other launchers, drivers, Windows components, Microsoft Store apps, and apps whose installed version is unknown. winget reports no available versions for Store apps, so the Store keeps them up to date.
  - It starts collapsed. Expanding it shows every one of these apps, and search filters them too.
  - One line explains that these apps update themselves or through another app, so they can't be tracked here.
  - Rows have no tick box. Each row names what updates the app when that's known, such as Steam, Microsoft Store, Windows Update or a driver tool.
- winget's full installed list misses some apps it can update. So the page also looks up the unmatched apps by name and id, and moves each app it finds into the tickable list. A match counts only when it's unambiguous: the names agree, the offered version isn't older than the installed one, and no other package fits as well. Otherwise the app stays under Updated elsewhere, because a wrong match could install a different edition over it.

### 4.5 Settings page

| Section | Setting | Default |
|---|---|---|
| Apps | Choose apps (N tracked) › | none tracked |
| Checking | Check for updates every: 1, 3, 6, 12 or 24 h | 6 h |
| Installing | Install updates without asking (silent mode; one admin approval to turn on) | Off |
| Installing | Wait before auto-installing: Off, 1, 3 or 7 days | Off |
| Installing | Pause during games and presentations | On |
| Installing | Limit download speed, then "Enter limit in kilobytes per second [____] KB/s", with the MB/s equivalent shown next to it | Off, 17500 when enabled |
| Notifications | Show notifications | On |
| General | Start with Windows | On (installer checkbox) |
| General | Shortcut to open (click to record, Esc to clear) | Ctrl + Alt + U |
| General | Update Software Update Tracker automatically | On |
| About | Version, GitHub link, license, Open logs folder, Copy diagnostic info | |

The footer has a **Quit** button.

### 4.6 History page
- Grouped by day. Each entry shows the result icon, app, "from → to" or the reason, and the time.
- The result is one of: Updated / Failed / Skipped / Cancelled. Failed entries have **Retry**.
- It keeps 90 days. The footer has **Clear history**.

### 4.7 Notifications
- Notifications are Windows toasts with buttons, shown under the app's own name and icon. Clicking the body opens the flyout.

| Trigger | Toast | Buttons |
|---|---|---|
| New versions (Auto off), once per version, batched per check | "3 updates ready" plus names | Update all, View |
| Auto-updates that need admin (default mode) | "2 updates need your permission" | Install, Later |
| Update failed because the app is open | "G HUB needs to close to update" | Close & update, Later |
| Batch finished | "3 updates installed" / "1 update failed" | View |
| Restart needed | "Restart to finish updating NVIDIA App" | none |
| Self-update available (auto self-update off) | "Software Update Tracker 1.1 is available" | Update, Later |

- Toasts are held back while a full-screen game or presentation runs, then shown afterwards.
- When notifications are off, the tray badge still shows what's pending.

### 4.8 Keyboard and accessibility
- The global shortcut opens the flyout (default Ctrl+Alt+U, configurable). If another app already owns the combination, Settings shows "Shortcut in use".
- In the flyout: arrow keys move between rows, Enter runs the primary action, Esc goes back or closes, and Tab order follows the layout.
- Every control has an automation name for Narrator. High contrast and text scaling are respected.

## 5. Architecture

### 5.1 Processes and trust boundaries

```
┌─ User session, normal rights ──────────────────────────────────────┐
│ SoftwareUpdateTracker.exe  (WinUI 3)                               │
│   tray icon · flyout · toasts · scheduler · hotkey · speed relay   │
│   ├─ Core    rules, scheduling, settings, history (no UI, tested)  │
│   └─ WinGet  COM client (list, metadata, upgrades) + CLI executor  │
└───────────────┬────────────────────────────────────────────────────┘
                │ named pipe, ACL: this user + Administrators only
┌───────────────▼─ Elevated (UAC per batch, or silent-mode task) ────┐
│ SoftwareUpdateTracker.Helper.exe  (no UI, stateless)               │
│   accepts: upgrade(id, source, expectedVersion), cancel,           │
│   setProxyOption(on/off), installSelfUpdate(verified file)         │
└────────────────────────────────────────────────────────────────────┘
```

- The tray app never runs elevated. It exits if launched elevated, relaunching unelevated first.
- The helper runs only while there is admin work. It holds no data and exits when its queue is empty.
- **Single instance:** a second launch (from the Start menu, the installer's "Launch now", or a toast activation) redirects to the running copy, which opens the flyout.

### 5.2 Repository layout

```
src/
  SoftwareUpdateTracker.App/      WinUI 3 tray app (views, view models, tray, windowing)
  SoftwareUpdateTracker.Core/     models, rules, scheduler, stores
  SoftwareUpdateTracker.WinGet/   COM adapter, CLI executor, throttling relay, error mapping
  SoftwareUpdateTracker.Helper/   elevated worker
tests/
  SoftwareUpdateTracker.Core.Tests/
  SoftwareUpdateTracker.WinGet.Tests/
  SoftwareUpdateTracker.App.Tests/      view-model tests
installer/SoftwareUpdateTracker.iss
assets/icon/                    hamster.svg, hamster-small.svg, generated .ico/.png
scripts/                        icon generation, leftover scan, resource check
docs/design/                    this document, mockup
docs/manual-test-checklist.md
.github/workflows/              ci.yml, release.yml
README.md  LICENSE  .gitignore  .gitattributes  Directory.Build.props  global.json
```

### 5.3 Key dependencies (all versions pinned)
- .NET 10 SDK and runtime, self-contained.
- Windows App SDK 2.5.1, pinned; it moves up only after a re-test.
- `Microsoft.WindowsPackageManager.ComInterop` 1.29.x, matching the minimum supported winget.
- Tray icon: raw `Shell_NotifyIconW` (NOTIFYICON_VERSION_4) on a hidden window; no third-party library.
- Toasts: `Windows.UI.Notifications` with our own HKCU `AppUserModelId` key. `AppNotificationManager` can't register in self-contained apps (WindowsAppSDK #6774), and the toolkit fallback pulls a vulnerable `System.Drawing.Common`.
- CommunityToolkit.Mvvm.
- xUnit for tests.
- Inno Setup 7.1.

### 5.4 Data (per user)
- `%APPDATA%\Software Update Tracker\settings.json` holds:
  - the settings
  - the tracked apps (id, source, Auto, skipped version)
  - bookkeeping: first-seen dates, cached release dates, last auto attempt, phantom flags
- `history.json` keeps the last 90 days.
- `logs\app.log` plus one rotated file, 1 MB each (2 MB cap).
- All writes are atomic: write to a temp file, then replace. A corrupt file is kept once as `.bak`, and defaults are used with a notice.
- All times are stored in UTC.

## 6. Behavior

### 6.1 Checking
- **Triggers:**
  - the schedule (every N hours)
  - 1 minute after sign-in
  - **Check now**
  - opening the flyout when data is more than 15 minutes old
  - the network returning after being offline, if a check was due
- **Gating:** scheduled checks skip while offline or while Battery saver is on, and run when the condition clears.
- **Each check:**
  1. List installed packages correlated with the winget catalog (COM). The msstore catalog isn't used: winget reports no available versions for Store apps, and every lookup there is a slow network call. Then look up by id any tracked app the list didn't match, because the full list misses apps that only a targeted lookup matches. Read installed and available versions.
  2. Filter out:
     - installed version "Unknown" (shown as "Version unknown")
     - skipped versions
     - phantom versions (§6.2)
  3. For each candidate, collect:
     - publisher, via COM
     - release notes URL, via COM
     - release date. The COM API doesn't expose it, so it's read from the package's public manifest in `microsoft/winget-pkgs` on GitHub, cached per version. If none is found, the first-seen date is used.
  4. Load icons into memory from each app's Windows uninstall entry (DisplayIcon / install location).
- winget must be version 1.29.280 or newer (security fix). If it's older or missing, the flyout shows a banner with a Microsoft Store link to App Installer.

### 6.2 Decision rules (Core, fully unit-tested)
- **Manual action:** Update or Update all queues immediately, ignoring the wait and game rules.
- **Auto on:** the app queues automatically when all of these hold:
  1. release age ≥ the wait setting (release date, else first-seen date)
  2. no full-screen app or presentation is running (`SHQueryUserNotificationState`)
  3. the connection isn't metered
  4. Battery saver is off
  5. no auto attempt on this version in the last 12 h
- **Auto off:** the app gets one toast per new version.
- **Phantom detection:** if a successful upgrade leaves the installed version unchanged, and winget still offers the same version, that version is flagged. It isn't auto-installed again, and the row offers **Update anyway**.

### 6.3 Install pipeline
- **One package at a time,** in a visible queue, because parallel installers conflict.
- **Routing,** from `GetApplicableInstaller()` Scope/ElevationRequirement and the installed scope:

| Case | Default mode | Silent mode |
|---|---|---|
| ElevationProhibited | App (unelevated) | App |
| Machine scope, or ElevationRequired | Helper, one UAC prompt per batch | Helper, no prompt |
| ElevatesSelf | App (the installer prompts itself) | Helper |
| Unknown | App | Helper only if the installed scope is Machine, otherwise App |

- **Progress** (COM `UpgradePackageAsync`): Queued → Downloading (bytes and %) → Installing (% if known) → Finished.
  - Speed is a rolling average over the last 3 s.
  - Cancel is allowed while Queued or Downloading.
- **App in use** (0x8A150101 or equivalent installer codes) is handled by **Close & update**:
  1. Find the app's processes under its install location.
  2. Close them gracefully (`WM_CLOSE`), waiting up to 10 s.
  3. If they're still running, ask before force-closing.
  4. Update.
  5. Reopen the app's main executable as the user, unelevated.
- **Reboot detection:** installer codes 3010/1641, or winget's reboot-required result, produce "Restart to finish". COM always reports `RebootRequired` as false, so it isn't used.
- **After each package:** re-query it to confirm the new version, write a History entry, and update the row.
- **Default mode, auto-updates needing admin:** these don't prompt on their own. They wait behind the "need your permission" toast and the row's **Install** button.

### 6.4 Download speed limit
- winget's COM API has no proxy option. The CLI supports `--proxy`, but only when the admin setting `ProxyCommandLineOptions` is enabled; it's off by default. The first time the user turns the limit on, the helper enables that setting, with one approval.
- **Limit on:** upgrades run through the CLI:
  `winget upgrade --id <id> --exact --source <src> --version <v> --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --proxy http://127.0.0.1:<port>`
  - Arguments are passed as a list, never built as a command string.
  - The proxy target is an in-app HTTP CONNECT relay that listens on 127.0.0.1 only, tunnels port 443 only, and never decrypts TLS. One global token bucket enforces the KB/s limit across all connections.
  - Progress comes from the relay's byte counter. The total size comes from a HEAD request on the installer URL; if that fails, it's shown as unknown.
- **Limit off:** upgrades use COM (§6.3).
- Helper-routed upgrades use the same CLI path and the app's relay while the limit is on.
- The limit also applies to self-update downloads.
- Known gap: web installers that fetch more data themselves are not capped. The README states this.

### 6.5 Self-update
- **Once a day,** the app calls the GitHub Releases API for `Bikuuuu/Software-Update-Tracker` and compares SemVer.
- **Downloading:** the app downloads `SoftwareUpdateTracker-Setup-<ver>-x64.exe` (throttled if the limit is on).
- **Installing,** done by the helper (one admin prompt in default mode, none in silent mode):
  1. Copy the file into `…\Software Update Tracker\update\` inside Program Files, which only admins can write.
  2. Verify the copy's SHA-256 against the asset digest that the helper fetches from the GitHub API itself. Once releases are signed, also verify the Authenticode signature.
  3. Keep the file open with writes denied.
  4. Run it with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS`. The app registers with Restart Manager (`RegisterApplicationRestart`), so it comes back after the upgrade.
  5. Delete the downloaded file.
- **Auto self-update on:** installs at an idle moment, respecting the game rule.
- **Auto self-update off:** a row appears at the top of Updates, plus a toast.
- Repo setting: **immutable releases** enabled.

### 6.6 Silent mode
- **Turning it on** registers the scheduled task "Software Update Tracker Helper" through the helper (one UAC prompt). The task:
  - runs `…\SoftwareUpdateTracker.Helper.exe --task`
  - has RunLevel Highest
  - runs only when the user is logged on
  - has no triggers; it's started on demand by the app through the Task Scheduler API
- **Turning it off,** or uninstalling, deletes the task.
- **Why it's safe:** the helper validates every request (§8). The worst a hostile local process could do by triggering it is update apps that are already installed.
- **Fallback:** if elevated winget is unavailable, for example with Windows *Administrator Protection* enabled, updates fall back to unelevated runs with per-installer prompts. A one-time tip explains this.

### 6.7 Startup, single instance, shortcut
- **Start with Windows:** a per-user `HKCU\…\Run` entry pointing to `SoftwareUpdateTracker.exe --startup`. The first check runs 1 minute later.
- The installer's "Start with Windows" and "Launch now" options pass through to the app, which runs unelevated (Inno `runasoriginaluser`).
- **Shortcut:** `RegisterHotKey` on a message-only window.

## 7. Error handling

Every error shows in plain words, with a **Details** button that reveals the code (HRESULT or installer exit code).

| Condition | Behavior |
|---|---|
| winget missing or older than 1.29.280 | Banner "winget needs an update" with a Store button |
| COM server not responding | Retry after 1, 5 and 15 min; status "Can't reach winget right now, retrying" |
| Offline | Checks skip quietly; they run when the network returns |
| Download stalled (no progress for 2 min) | Cancel, retry once, then "Download stalled" |
| Another install in progress (0x8A150102 / MSI 1618) | Retry up to 3× at 2 min intervals |
| Installer running over 30 min | Move on; mark "Took too long" |
| App in use | Close & update (§6.3) |
| Restart required | "Restart to finish" plus a toast |
| UAC declined | Items return to Available with "Permission was declined". No re-prompt until the user acts |
| Elevated winget unavailable | Unelevated fallback (§6.6) |
| Package uninstalled / no longer in winget | "Not installed anymore" / "Not found in winget", with Stop tracking |
| Helper crash or pipe loss | Remaining items are marked for retry; nothing stays stuck |
| App crash | Restarted via `RegisterApplicationRestart` |
| Corrupt settings | `.bak` kept once, defaults loaded, notice shown |

## 8. Security and privacy

- **Least privilege:** the UI is never elevated. The helper is elevated only while it has work, and it is stateless.
- **Helper input validation:**
  - It accepts only the listed request types.
  - For `upgrade`, it re-queries the package by id and source. It checks that the package is installed, that the update is applicable, and that the version matches. It never accepts installer arguments, overrides or paths.
  - For `installSelfUpdate`, it downloads the digest itself and verifies the file (§6.5).
- **Pipe:** the named pipe's DACL grants only the user's SID and Administrators. The helper validates messages regardless of who sent them.
- **Tamper resistance:** binaries live in Program Files, which only admins can write.
- **No command injection:** processes start with argument lists, and package ids are validated against winget's id grammar.
- **Relay:** listens on loopback only, allows CONNECT to 443 only, and exists only while a limited download runs.
- **Closing apps:** graceful first; force-close only after explicit consent.
- **Privacy:**
  - No telemetry, no accounts, no ads.
  - Network traffic goes only to the winget sources and installer hosts (as winget itself does) and to GitHub (release dates, self-update).
  - No personal data leaves the PC.
- **Repo hygiene:** Dependabot and CodeQL enabled. Actions pinned by SHA. Signing secrets (later) live in GitHub encrypted secrets.

## 9. Resource budgets

These are measured with `scripts/resource-check.ps1` before each release. A release is blocked if a budget is missed.

| Condition | Budget | Technique |
|---|---|---|
| Idle, flyout closed | CPU ≈ 0% (< 0.1% average over 30 min) | Event-driven only: one timer for the next check, no polling |
| Idle, flyout closed | RAM < 40 MB in Task Manager | Working set trimmed after the flyout hides |
| Idle, flyout closed | GPU 0% | Cloaked window, no running animations |
| Idle | Efficiency mode (EcoQoS) on | `SetProcessInformation(ProcessPowerThrottling)`; turned off while the flyout is open or work runs |
| Check | A few seconds of CPU every N hours | Mostly winget's own work |
| Flyout open | Smooth 60 fps motion | Composition animations only |

## 10. Disk footprint and cleanup

| Item | Location | Size or lifetime |
|---|---|---|
| App binaries (self-contained) | `C:\Program Files\Software Update Tracker\` | about 100 MB |
| Settings, history | `%APPDATA%\Software Update Tracker\` | KB |
| Logs | same folder | ≤ 2 MB |
| Icons | memory only | none on disk |
| Downloaded installers | winget's own temp folder, deleted by winget; the app keeps none | transient |
| Self-update file | `update\` inside the Program Files folder (admin-only), deleted after install. Leftovers are removed on the next start | transient |

- **Registry and system entries:**
  - the uninstall entry
  - the HKCU Run value (if Start with Windows is on; removed for the user who uninstalls)
  - the toast registration (AUMID / COM activator)
  - the silent-mode task (only while silent mode is on)
  - winget's `ProxyCommandLineOptions` (only while the speed limit is on, and only if the app enabled it)
- **Uninstall** removes all of the above, restores `ProxyCommandLineOptions` to its previous state, and asks: "Also remove your settings and history?" (default Yes; silent uninstalls remove them).
- **Outside our control, stated in the README:** winget's own logs, and leftovers created by third-party installers.

## 11. Releases and distribution

- **Versioning:** SemVer, starting at 1.0.0. A `vX.Y.Z` tag triggers `release.yml`.
- **CI** (`ci.yml`, on push and PR, runner `windows-2025`): restore → build (Release, x64) → unit tests → safe read-only winget integration tests (skipped if winget is unavailable on the runner).
- **Release** (`release.yml`):
  1. build, test, and `dotnet publish` self-contained win-x64
  2. install Inno Setup 7.1 on the runner and compile `Setup.exe`
  3. signing slot, disabled until SignPath approval
  4. install tests on the runner (§12)
  5. GitHub Release with `Setup.exe`, with immutable releases on
- **Installer:**
  - per-machine (`PrivilegesRequired=admin`), with a Start menu entry
  - last page: "Start with Windows" and "Launch now"
  - silent flags supported, so winget-pkgs can list it
  - the uninstaller performs the cleanup in §10
- **Signing:** apply to SignPath Foundation after 1.0 (requirements: OSI license, public repo, CI builds, code-signing policy page). If declined, stay unsigned. The README explains the SmartScreen "More info → Run anyway" step, and that Smart App Control blocks unsigned apps.
- **README:**
  - what it does, with a screenshot
  - install steps and the SmartScreen note
  - features and privacy
  - building from source
  - license
- **After 1.0:** submit to `winget-pkgs` with wingetcreate, then automate later versions with winget-releaser.

## 12. Testing

| Layer | Scope | Where |
|---|---|---|
| Unit (the bulk, test-first) | Version compare and diff, decision rules, scheduler, sleep and resume, settings load/save/recovery, history retention, error-code mapping, token bucket accuracy, self-update verification logic, helper request validation | CI, every push |
| View-model | State → texts, buttons and progress for every row state in §4.3 | CI, every push |
| winget integration | Read-only listing and metadata against real winget | Locally; on CI if winget exists |
| Install tests | Silent install → app `--self-test` → upgrade an older version of a small test package through the app's pipeline → silent mode task on/off → speed-limit path → uninstall → leftover scan (file, registry and task snapshot diff) | GitHub-hosted runner, each release |
| Resource check | §9 budgets | A real PC, before each release |
| Manual checklist | `docs/manual-test-checklist.md`: real UAC prompt, dark/light mode, accent colors, 100/125/150/200% scaling, two monitors, keyboard-only, Narrator, high contrast, game deferral, metered connection, Battery saver, UAC declined, self-update from the previous version, leftover scan after uninstall | A real PC, before each release |

The runner limitations (Windows Server, no UAC prompt, no real desktop visuals) are covered by the manual checklist.

## 13. Risks and early spikes

Spikes run first. Each one ends with a keep-or-change decision recorded in the plan.

| # | Question | Fallback |
|---|---|---|
| S1 | WinUI 3 flyout: cloaked instant show, acrylic, rounded corners on a non-resizable window, size to content, DPI, no flicker, deactivate-to-hide. Follow PowerToys Quick Access (MIT) | DWM attributes by hand; custom sizing |
| S2 | Tray icon library: H.NotifyIcon.WinUI vs WinUIEx TrayIcon, including DPI-correct icons and left/right click | Raw `Shell_NotifyIcon` interop |
| S3 | Toasts with buttons in an unpackaged self-contained app (WindowsAppSDK #6774 fix) | Microsoft.Toolkit.Uwp.Notifications |
| S4 | winget COM from the unelevated app: list, metadata, upgrade with progress and cancel | CLI executor (§6.4) |
| S5 | winget COM from the elevated helper (manual activation); behavior under Administrator Protection | CLI in the helper; unelevated fallback |
| S6 | CLI `--proxy` through the CONNECT relay really caps winget downloads | Drop the limit to self-update only, and tell the user |
| S7 | Baseline idle RAM, CPU and GPU of a minimal WinUI 3 tray app with trimming and EcoQoS | Tune; revisit budgets with the user |
| S8 | winget availability on the `windows-2025` runner | Install App Installer in the workflow |

Outcomes (2026-09-25) are recorded in [spikes.md](spikes.md). Two of them change the design: the flyout is not always-on-top (WinUI 3 windows refuse `WS_EX_TOPMOST`; focus from the tray click covers it), and S3 uses `Windows.UI.Notifications` directly (§5.3).

## 14. Delivery phases

1. **Spikes S1–S8**, then the project skeleton, CI and icon assets.
2. **Core:** models, rules, stores and scheduler (test-first).
3. **WinGet adapter:** listing, metadata, COM upgrades and error mapping.
4. **Tray and flyout:** Updates page with manual updates and live progress.
5. **Choose apps, Settings and History pages.**
6. **Automation:** auto-update rules, notifications, game, metered and battery gating, and the shortcut.
7. **Admin:** helper, pipe, batching, silent mode and Close & update.
8. **Speed limit:** relay and CLI executor.
9. **Distribution:** installer, self-update, release workflow, install tests, README and manual checklist.
10. **Polish:** motion, accessibility and resource budgets. Then 1.0, public repo, SignPath application, and winget-pkgs.
