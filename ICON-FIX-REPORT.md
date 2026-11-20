# Application-icon fix — report (2026-11-20)

Owner report: "when the program installs, there is no icon for it on the
desktop — the icon has no look." Windows showed the blank default icon.

## Diagnosis (verified before changing anything)

- The desktop shortcut `EsoAgent.lnk` targets
  `C:\Program Files\EsoAgent\EsoAgent.App.exe` with an **empty
  IconLocation**, so it falls back to the exe's embedded icon — and no
  exe had one: **no csproj in `src/` set `<ApplicationIcon>`**, and the
  `[Icons]` entries in `installer/windows/EsoAgent.iss` set no
  `IconFilename`.
- The only `.ico` files in the repo were the small tray **status** icons.
  The brand's tile renders (`assets/brand/png/app-icon-*.png`) existed,
  and an older `assets/brand/png/app-icon.ico` existed too, but nothing
  referenced either as an application icon (the `.iss` used that old ICO
  only as the setup wizard's own `SetupIconFile`).

## The icon

- **Source:** the GOLDEN WING brand geometry in
  `scripts/render-brand.py` (the single source of truth for the brand) —
  `render_tile()`: gold wing + signal waves centred on the matte-black
  rounded tile, the same render path and look as the shipped tray icons
  and `app-icon-512.png`. No new artwork, no external asset.
- **Master:** `assets/brand/app.ico` (44,144 bytes), rendered natively at
  each size (supersampled geometry, not downscales of one bitmap).
  `render-brand.py` now emits it in `main()` (and copies it into the App
  project), so regeneration keeps it in sync; `assets/brand/README.md`
  documents it.
- **Verified by parsing the ICO directory** — 8 entries, all 32 bpp, all
  PNG-compressed:

  | Size | Bytes | Format |
  |---|---|---|
  | 16×16 | 642 | PNG |
  | 20×20 | 871 | PNG |
  | 24×24 | 1,067 | PNG |
  | 32×32 | 1,520 | PNG |
  | 48×48 | 2,607 | PNG |
  | 64×64 | 3,872 | PNG |
  | 128×128 | 10,093 | PNG |
  | 256×256 | 23,338 | PNG |

- **Eyeball check:** the extracted 256 entry shows the wing centred on
  the matte-black tile, identical in character to `app-icon-512.png`;
  the 16/20/32 entries stay legible as the gold wing glyph, consistent
  with the tray icons at those sizes.

## Changes

| File | Change |
|---|---|
| `assets/brand/app.ico` | **New master** (above). |
| `scripts/render-brand.py` | `main()` now also writes `assets/brand/app.ico` (16–256 tile renders) and copies it to `src/EsoAgent.App/Assets/app.ico`; header docs updated. |
| `assets/brand/README.md` | Layout section documents `app.ico` and its three consumers. |
| `src/EsoAgent.App/Assets/app.ico` | Copy of the master (Avalonia resource + project-local reference). |
| `src/EsoAgent.App/EsoAgent.App.csproj` | `<ApplicationIcon>..\..\assets\brand\app.ico</ApplicationIcon>`; `Assets\app.ico` added as an `AvaloniaResource`. |
| `src/EsoAgent.App/MainWindow.axaml` | Window `Icon` now `avares://EsoAgent.App/Assets/app.ico` (was the single 48×48 `app-icon.png`) — taskbar/Alt-Tab pick the right size from the multi-res ICO. One attribute; no code touched. (The in-window header logo still uses `app-icon.png`.) |
| `src/EsoAgent.Tray/EsoAgent.Tray.csproj` | `<ApplicationIcon>` added — the **exe file icon only**; the runtime tray status icons (`Assets\*.ico` embedded set) are untouched. |
| `src/EsoAgent.Windows/EsoAgent.Windows.csproj` | `<ApplicationIcon>` added (service exe). |
| `src/EsoAgent.Client/EsoAgent.Client.csproj` | `<ApplicationIcon>` added (`eso-client.exe`). |
| `src/EsoAgent.Server/EsoAgent.Server.csproj` | `<ApplicationIcon>` added (`eso-server.exe`). |
| `installer/windows/EsoAgent.iss` | [Files] ships `..\..\assets\brand\app.ico` into `{app}`; both App shortcuts (Start Menu + Desktop) now set `IconFilename: "{app}\app.ico"`; `SetupIconFile` repointed from the old `png\app-icon.ico` to the new master `..\..\assets\brand\app.ico` so the setup wizard carries the same icon. |

Deliberately **not** covered: `EsoAgent.Linux` — ELF binaries have no
Win32 icon resource; the property would be meaningless there.
`EsoAgent.Core` is a library (no exe).

**install.ps1:** checked `install/install.ps1` (and the staged copy in
`dist/pkg-windows/`) — **it creates no shortcuts at all.** Its only
icon-adjacent artefact is the HKCU `Run` autostart value for the tray,
which has no icon concept. Nothing to align; no `.ps1` was modified.

Untouched, per scope: the Phase 0+1 web assets, the four panel HTML
files, `EsoTokens.axaml`, and the single-instance fix
(`EsoAgent.Tray/Program.cs`, `EsoAgent.App/SingleInstance.cs`,
`EsoAgent.App/Program.cs`, `App.axaml.cs` — none modified).

## Build & tests (sandbox)

- `dotnet build EsoAgent.sln` — **succeeded, 0 warnings, 0 errors.**
- Tests via the xunit console runner (vstest sockets are broken in this
  sandbox): **Total 184, Failed 4** — and the failing set is exactly the
  pre-existing sandbox-environment baseline, name-for-name:
  - `RelayDesktopTests.RelayOpen_HelloIsSeededAsFirstChannelMessage`
  - `RelayDesktopTests.RelayOpen_RunnerConsumesSeededHelloAndOpensSession`
  - `PackageUploadLimitTests.SharedLimit_ExceedsKestrelDefaultAndRealPackages`
  - `EfficiencyTests.PackageVersion_StampRoundTrip`
- **Embedding proof (win-x64 publish, sandbox):** published
  `EsoAgent.App` and `EsoAgent.Tray` for `win-x64`; the resulting
  `EsoAgent.App.exe` and `EsoAgent.Tray.exe` both contain the ICO's
  256×256 PNG entry **verbatim** in their PE resources — the icon is
  really in the exes, not just named in the projects. The published
  `EsoAgent.App.dll` likewise contains the full `app.ico` as the
  Avalonia resource `Assets/app.ico`, so the window icon resolves at
  runtime.

## Ship-time verification (on the laptop, after the release ships)

1. Install the new build **fresh** (uninstall the old one first, or use
   a clean Windows user) and look at the **Desktop** and **Start Menu**:
   the EsoAgent shortcut shows the golden wing on the black tile — not
   the blank page icon. (The shortcuts name `{app}\app.ico` explicitly,
   and `{app}\app.ico` itself is present in `C:\Program Files\EsoAgent`.)
2. In Explorer, `C:\Program Files\EsoAgent\EsoAgent.App.exe` (and
   `EsoAgent.Tray.exe`, `EsoAgent.Windows.exe`) show the wing as their
   file icon; Apps & Features shows the wing for EsoAgent (it reads
   `UninstallDisplayIcon` → the App exe).
3. Launch the app: the **taskbar button and Alt-Tab** show the wing
   while it runs (multi-res window icon), and the title-bar icon is the
   wing.
4. The tray **status** icons are unchanged by design (online gold /
   offline grey / controlled pulse / locked slate) — this fix only gave
   the executables and shortcuts their file icon.

**Icon-cache caveat:** Windows caches icons per path
(`%LocalAppData%\Microsoft\Windows\Explorer\iconcache_*.db`). The
laptop's current install already cached the *blank* icon for
`C:\Program Files\EsoAgent\EsoAgent.App.exe`, so an **in-place upgrade
at the same path can keep showing the cached blank icon** for the exe
itself until the cache refreshes — that is a Windows cache artefact,
not a missing icon. The verification handles it three ways: (a) judge
the **shortcuts** first — they point at the new, never-cached
`{app}\app.ico`, so they render fresh immediately; (b) for the exe
icon, either verify on the fresh install/clean user from step 1, or
clear the cache (close Explorer, delete `iconcache_*.db`, restart
Explorer — or simply reboot) before judging; (c) if a shortcut still
shows blank after a reboot, delete and let the installer recreate it —
do not conclude the icon is missing from a stale cache.
