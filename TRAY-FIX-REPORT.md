# Tray-icon duplication fix — report (2026-11-20)

Owner report: "every time I open the program and close it, a new icon
appears down below — lots of duplicates, not just one icon."

## Diagnosis (verified in source, not assumed)

There are **two** tray icons by design (items 42/43), and **neither
executable had any single-instance guard** — `grep Mutex|SingleInstance`
over `src/**/*.cs` returned zero hits before this fix:

1. **`EsoAgent.Tray.exe`** (WinForms companion): `Program.Main`
   unconditionally ran a new `TrayContext`, i.e. a new `NotifyIcon` and a
   new `EsoAgentTrayPipe` server per launch. Its `Dispose` hides/disposes
   the icon correctly — the bug was pure multiplication of instances.
2. **`EsoAgent.App.exe`** (Avalonia client): `AppTray` installs its own
   `TrayIcon`, and the app is **close-to-tray by design**
   (`ShutdownMode.OnExplicitShutdown`, `MainWindow.OnClosing` hides the
   window; only the tray menu's "Quit EsoAgent" exits). So the owner's
   exact loop — open the program, close it, open it again — started a
   **new App process with a new tray icon every time**, while the
   "closed" copies kept running hidden. This is the main pile-up source.

## Launch-path map (who spawns what, and when)

`EsoAgent.Tray.exe`:
- **HKCU Run key** `EsoAgentTray` — written by `install/install.ps1:103`
  and by the Inno installer (`installer/windows/EsoAgent.iss:82-83`).
  Fires once per logon.
- **Manual starts** (double-clicking the exe, ad-hoc shortcuts).
- Nothing else: no service, script, or app code starts the tray.
  `install/update-apply.ps1` replaces the binary during updates but never
  launches it; `EsoAgent.Windows` only *connects* to `EsoAgentTrayPipe`
  (`TrayPipeForwarder.cs`, `DesktopBridge.cs`) and reports it missing.

`EsoAgent.App.exe`:
- Desktop / Start-menu shortcuts and manual launches.
- **The tray companion's "فتح التطبيق" menu item**
  (`TrayContext.OpenApp()` → `Process.Start(EsoAgent.App.exe)`).

## Changes

| File | Change |
|---|---|
| `src/EsoAgent.Tray/Program.cs` | `Main` now first acquires the session-local named mutex `Local\EsoAgent.Tray.SingleInstance` (`WaitOne(0)`, `AbandonedMutexException` treated as acquired). A second instance returns immediately — no window, no message, no icon, no second pipe server. The mutex is held for the whole `Application.Run` and released on exit. |
| `src/EsoAgent.App/SingleInstance.cs` | **New.** Static helper holding `Local\EsoAgent.App.SingleInstance` (mutex, same pattern) plus the named auto-reset event `Local\EsoAgent.App.ShowWindow`. `TryAcquire()` returns false for a later launch after signalling the running instance; fails open on platforms without named sync primitives. `StartShowListener(Action)` runs a background thread that invokes the callback per later launch. |
| `src/EsoAgent.App/Program.cs` | `Main` calls `SingleInstance.TryAcquire()` first; a duplicate launch exits quietly (after the signal) instead of building a second Avalonia app. |
| `src/EsoAgent.App/App.axaml.cs` | After `AppTray.Initialize(...)`, starts the show listener: `Dispatcher.UIThread.Post(AppTray.ShowWindow)` — a re-launch surfaces the one existing window (un-hide + activate) instead of opening a copy. |

No `.ps1`/`.iss` changes, deliberately: neither script ever spawns the
tray directly (the installer only registers the Run key, and a registry
autostart cannot check a mutex), and both real spawn paths — the Run key
and the companion's OpenApp item — land on executables that now guard
themselves. The guard lives at the one point every path must pass.

Notes / trade-offs:
- Mutex names use the `Local\` prefix: one instance **per user session**,
  so a second logged-on user still gets their own tray/app — correct for
  a per-session tray companion and a per-session pipe.
- The tray's second instance exits silently with no signal: the tray has
  no window to surface, its icon is already visible, and its status poll
  runs every 3 s anyway — signalling would add fragility for no benefit.
- The two by-design icons remain two: the companion's status icon and the
  App's icon. This fix stops each from multiplying; it does not merge
  them.

## Build & tests (sandbox, verified before AND after)

- Baseline (pre-change, this session): `dotnet build EsoAgent.sln` —
  succeeded, 0 warnings, 0 errors. Tests via xunit console runner
  (vstest sockets are broken in this sandbox): **Total 184, Failed 4**.
- After the change: build succeeded, **0 warnings, 0 errors** (an initial
  CA1416 platform warning on `EventWaitHandle.TryOpenExisting` was fixed
  with an `OperatingSystem.IsWindows()` guard, not a suppression).
  Tests: **Total 184, Failed 4**.
- The failing sets were captured to files and `diff`ed: **byte-identical**
  before and after. The 4 are the known sandbox-environment failures:
  - `RelayDesktopTests.RelayOpen_HelloIsSeededAsFirstChannelMessage`
  - `RelayDesktopTests.RelayOpen_RunnerConsumesSeededHelloAndOpensSession`
  - `PackageUploadLimitTests.SharedLimit_ExceedsKestrelDefaultAndRealPackages`
  - `EfficiencyTests.PackageVersion_StampRoundTrip`
- Scope check: the only files changed are the 4 listed above. No backend,
  API, route, or Phase 0+1 web asset (`src/EsoAgent.Web/*`, the four panel
  HTML files, `Themes/EsoTokens.axaml`) was touched.

## On-device verification procedure (for ship time, on the laptop)

After installing the build containing this fix:

1. Log on (the Run key starts the companion). Check: exactly **1**
   `EsoAgent.Tray` process and **1** companion icon in the tray.
   `Get-Process EsoAgent.Tray | Measure-Object` → Count = 1.
2. Manually launch `EsoAgent.Tray.exe` two more times. Still **1**
   process, **1** icon; the extra launches exit invisibly.
3. Open the App from its shortcut → window + **1** App tray icon. Close
   the window with **X** (it hides to tray). Re-open from the shortcut —
   repeat **3 times**: each time the *same* window comes forward,
   `Get-Process EsoAgent.App` stays at **1**, and the App icon count
   stays **1**.
4. With the App running, use the companion tray menu's "فتح التطبيق":
   no second App process appears; the existing window surfaces.
5. Quit via the App tray menu's "Quit EsoAgent": the App process and its
   icon disappear; a fresh shortcut launch starts it cleanly again.
6. End state after all cycles: exactly **2** EsoAgent icons in the tray
   (companion + App, by design) — and the count never grows no matter
   how many open/close cycles are repeated.
