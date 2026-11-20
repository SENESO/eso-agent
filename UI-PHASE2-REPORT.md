# EsoAgent UI Overhaul — Phase 2 Report (App shell & first-run pairing)

**Date:** 2026-11-20 · **Scope:** Phase 2 from `UI-OVERHAUL.md`, as scoped by
the parent task: (1) App shell on the EsoTokens theme with a TeamViewer-style
home, (2) first-run pairing wizard replacing the raw 7-field editor as the
default path, (3) wiring to existing client/fleet plumbing only.
**Status:** implemented and measured; NOT deployed, NOT committed — parent
reviews and ships.
**Constraint compliance:** changes are confined to `src/EsoAgent.App`
(10 files, listed in §5). No Core/Server/Windows/Tray file was touched; no
API, route, payload, or protocol changed; the Phase 0+1 web assets and
panel HTML are untouched (mtimes predate this session). The single-instance
fix is preserved byte-for-byte (`SingleInstance.cs`, `Program.cs`,
`App.axaml.cs` unmodified), as is the window-icon wiring in
`MainWindow.axaml` (`Icon="avares://EsoAgent.App/Assets/app.ico"` kept).

---

## 1. What was built

### 1.1 App shell on EsoTokens (`App.axaml`, `MainWindow.axaml`)

- `App.axaml` now merges `Themes/EsoTokens.axaml` into application
  resources, so every shell surface resolves the shared tokens
  (`EsoBgBrush`, `EsoSurfaceBrush`, `EsoAccentBrush`, …) via
  `DynamicResource`. New shell styles (`Button.accent`, `Border.card`,
  `TextBlock.muted`) are built only on token values.
- **Theme-variant decision (deviation, deliberate):** the audit's fixed
  design decision §3.1.3 says the App ships the refined-dark variant by
  brand, with the light variant behind the same tokens following the OS.
  `RequestedThemeVariant` is therefore `Default` (follow the OS) instead
  of the previous forced `Dark`: on a light system the shell renders in
  the EsoTokens **light** theme; on a dark system in the refined-dark
  variant — one token switch, no per-screen forks. Forcing light
  unconditionally is a one-line change in `App.axaml` if the owner
  prefers it over the brand/OS behaviour.

### 1.2 The home (left column of `MainWindow`)

The home screen is now the connect flow (audit §3.1 decision 5):

- **Identity card (this device, front and centre):** the console name
  (wizard-set, else the machine name) at 20 px; **the device code at
  26 px bold** when this computer's own agent is enrolled in the paired
  fleet — resolved client-side by `PairingSupport.FindThisDevice`
  (local-IP match against the fleet devices' recorded addresses first,
  machine-name match second; an agent enrolls under its machine name by
  default). Detail line shows online state + mesh VIP. When this computer
  is not an enrolled device, the card honestly shows the pairing state
  (fleet URL + reachable/not + "controls other devices" note, direct
  endpoint, or the not-paired hint) instead of inventing a code.
  A **Pair / set up** button on the card opens the wizard at any time.
- **Connect by code:** a code field + Connect button (Enter works too).
  The code is validated against the existing `DeviceIdentity.LooksLikeCode`
  format, resolved against the loaded fleet list (one refresh + retry if
  absent — the list may be stale), and a hit runs the same one session
  path as every other affordance. Unknown/pending/rejected codes get a
  plain-language state line, in EN/AR.
- **Device list:** rows keep name/detail/status/badges and gain a
  per-device **Connect** button (accent) on approved/direct devices, an
  **Approve** button on pending devices (the panel's 1-click F7 action,
  now in the App, via the existing registry action), and a **★/☆ pin**
  toggle. Ordering: pinned first, then online, then name.
- **Search + group filter** above the list (name/code/OS/groups text
  match; group combo populated from live data).
- **Recents:** the last 8 devices a session was started on, as chips
  under the connect-by-code row; one click reconnects.
- Favourites and recents persist in the existing `app-settings.json`
  beside the client config (merge-written; the language/tray-hint keys
  are untouched).

Every session start — row Connect, double-click, recent chip,
connect-by-code — funnels into one method: select the device (attaching
all tabs), show the Desktop tab, and call the new
`DesktopView.StartSessionAsync()`, which is the pre-existing
`ConnectAsync` path (direct → config-address → fleet relay) under a
public name. No session logic was duplicated or altered.

### 1.3 Pairing wizard (`Views/PairingWizard.axaml(.cs)`, `PairingSupport.cs`)

Opens automatically on first run (client config missing/unusable —
detected in `MainWindow`'s existing startup, after single-instance and
tray init, whose semantics are unchanged) and from the identity card's
Pair button. Three steps, token-styled, fully EN/AR via `Strings`:

1. **Role** — Control other devices / Both / Only be controlled, each
   with a one-line description. "Be controlled" shows the honest host
   guidance (see §3.3) inline.
2. **Fleet or standalone.**
   - *Fleet:* the user types **only the fleet address**. **Find the
     fleet** opens one TLS connection and learns the server certificate's
     SHA-256 (`PairingSupport.ProbeFingerprintAsync`) — the fingerprint
     is never typed. The wizard shows a short **verification code**
     (first 10 hex digits grouped + last 4) to compare against the fleet
     panel's Access tab value; **It matches — continue** is the security
     gate (editing the address afterwards invalidates the learned
     identity and forces a re-probe). Then the operator key: an existing
     configured/default key is loaded silently; otherwise **Create my
     operator key** performs the `eso keygen` operation in place
     (ECDSA P-256, PKCS#8 PEM, never overwritten) and shows only the
     **public** key, with a Copy button, for the fleet owner to add.
     **Test connection** runs the ordinary signed `GetDevicesAsync`
     through `FleetApiClient` and reports the device count. Next is
     gated on the verification-code confirmation, not on the test (the
     owner may not have added the key yet — the config is still correct,
     because the pin came from the server's own certificate).
   - *Standalone:* no fields; explains that the app will use a direct
     device from Advanced if one exists, and that a fleet can be joined
     later.
3. **Name this computer → Finish.** The name persists as the console
   name shown on the home. Finish writes the fleet fields into the
   shared `client.json` via the existing `AppConfig` (preserving any
   direct-device fields; for a fleet-only install the host is pointed
   at the fleet host because `ClientConfig.Load` requires one — direct
   calls to it simply fail auth, all fleet paths are unaffected).

The **Advanced** entry (wizard footer) opens today's `SettingsWindow`
byte-for-byte unchanged in behaviour; if it saves, the wizard closes and
the shell reloads. The header Settings button likewise still opens the
same editor — the demotion is that it is no longer the first-run or
only path, and the not-paired state no longer points at it.

### 1.4 Strings

`Strings.cs` grows from 27 to 82 EN/AR pairs: all home chrome (identity,
connect-by-code, search, approve, recents, pairing states) and the full
wizard follow the existing table and the shell's language toggle,
including flow-direction flip. The shell re-renders device rows on a
language switch so row action labels follow.

---

## 2. Measurements — before → after (audit §1.5 / §3.4)

| Flow | Before (audit baseline) | After Phase 2 | Target | Verdict |
|---|---|---|---|---|
| F1 Pairing | **Not completable in the App** — 7 typed technical fields in Settings: host, port, **SHA-256 fingerprint**, **base64 token**, fleet URL, **fleet SHA-256 fingerprint**, **key-file path** | **3 wizard steps, 0 hashes typed.** Fleet path pointer actions: role (1) → Next (1) → Probe (1) → "It matches" (1) → [Create key (1) only if no key exists] → Next (1) → Finish (1) = **5–6 clicks**; the only typing is the fleet address (and optionally the computer name). Fingerprint learned from the server cert; key generated in place; only a public key is ever displayed | 3 steps, 0 hashes typed | ✓ |
| F2 App desktop connect | **2–3** (select device → Desktop tab → Connect) | **1** — the row's Connect button; also 1 via double-click, recent chip, or connect-by-code | 1 | ✓ |
| Connect by code (new) | did not exist | Type code (typing) → **1 click** (or Enter) → session starts through the existing direct/relay path | type code → 1 click | ✓ |
| F7 Approve a pending device (App) | not available in the App (panel only) | **1 click** on the pending row's Approve button; list refreshes | 1 | ✓ |
| F13-style identity display | no home identity anywhere in the App | This computer's name + code (26 px) + status on the home, 0 clicks to see, when enrolled | — | ✓ |

Counts are discrete pointer actions per the audit's convention, read
from the implemented flows (the app cannot be launched in this sandbox —
no display; see §4 for the on-device walkthrough that re-measures them
live).

---

## 3. Stopped points — capabilities with no existing client plumbing

Per instructions, these were NOT invented around; each stopped at the
existing plumbing's edge:

### 3.1 "Set/confirm the owner password" (task step 3) — no such concept in the App
The App is the **operator tier**: it authenticates with the ECDSA
operator key, and no code path in `EsoAgent.App`/`EsoAgent.Client` reads,
sets, or confirms an owner password. The owner password is the
**agent/panel tier** credential (agent dashboard + fleet panel login,
owner-only Access powers) and is deliberately separate — the fleet
client's own docs state owner-reserved actions do not exist on the
operator path. Inventing an owner-password field in the wizard would
have created a credential nothing verifies. Step 3 therefore follows
the audit's own F1 spec ("name this computer → Done") and confirms the
pairing with a live signed test instead. **If the owner wants the App to
manage the panel owner password, that is a backend/API decision for the
parent, not a UI step.**

### 3.2 Pairing payload / scan — no format exists
The task allowed "scan/paste a pairing payload **if the codebase already
has one**". It does not: a full-tree search finds no pairing-string,
`eso://` URI, QR, or combined config payload for operators (the only
enrollment keys in the product are for *devices* joining a fleet, a
different flow). The wizard therefore uses address + cert probe +
verification code, which achieves the same "0 hashes typed" property
with existing mechanisms. A paste-a-string payload would need a
generator on the panel/server side (backend work, out of scope).

### 3.3 "Be controlled" role — the App has no path to the local agent
Nothing in the client plumbing lets the App enroll, configure, or even
read the local agent: the agent's identity lives in its own data dir and
its tray pipe, which the App has never consumed (reading another
program's private key file from the UI would be wrong even where paths
coincide). The wizard's host role therefore gives the accurate guidance
(the agent service + tray own being controlled; the code lives there)
and still pairs the console; once paired, this computer's host identity
**does** appear on the home through the fleet list (§1.2). Enrolling the
local agent from the App would need a new local IPC/API — a parent
decision, not a view.

### 3.4 Remaining audit Phase-2 items outside this task's enumerated scope
The audit's Phase 2 line also lists the Status/Activity/Access rebuilds
(cards/tables instead of monospace dumps). This task's scope enumerated
the shell, the home, and the wizard; those three views are **unchanged**
and their text dumps remain, flagged here rather than hidden. They are
the natural next App work item (the fleet panel's Phase 1 equivalents
already show the target components).

---

## 4. Build / tests / verification

| Check | Result |
|---|---|
| `dotnet build EsoAgent.sln` (full, `--no-incremental`) | **green, 0 warnings, 0 errors** (an initial `ToggleButton.Checked` obsolescence warning set was fixed by moving to `IsCheckedChanged`; final build is warning-free) |
| Test suite (xunit console runner, sandbox workaround) | **184 total, 180 pass, 4 failed — name-for-name identical to the pre-change baseline** (measured before and after): `RelayDesktopTests.RelayOpen_HelloIsSeededAsFirstChannelMessage`, `RelayDesktopTests.RelayOpen_RunnerConsumesSeededHelloAndOpensSession`, `PackageUploadLimitTests.SharedLimit_ExceedsKestrelDefaultAndRealPackages`, `EfficiencyTests.PackageVersion_StampRoundTrip` — all pre-existing sandbox-environment failures |
| Scope audit (file mtimes) | Only `src/EsoAgent.App` files changed/created in this session; `SingleInstance.cs`, `Program.cs`, `App.axaml.cs`, `Themes/EsoTokens.axaml`, all web assets and all Core/Server/Windows/Tray sources predate it untouched |
| XAML compiled bindings | Validated by the build (binding paths + template event handlers are compile-checked in this project) |

**Not verified here:** live UI behaviour — the sandbox has no display,
so the app was never launched. Dynamic-resource theme resolution, the
template button wiring at runtime, and the wizard's probe against a real
fleet server must be confirmed on the owner's machine (procedure below).

### On-device verification procedure (ship time, owner's Windows)

1. **First run:** delete/rename `%USERPROFILE%\.esoagent\client.json`,
   launch the App → the wizard opens on its own (step 1). Pick *Both* →
   step 2: enter the fleet address of the Simular-box server → **Find
   the fleet** → compare the verification code with the fleet panel's
   Access-tab fingerprint (first 10 + last 4 hex must match) → **It
   matches** → create/copy the operator key if prompted → **Test
   connection** shows the fleet's device count → Next → name the
   computer → **Finish**. The device list fills without touching
   Settings. Count the clicks against §2 (expect 5–6 + typing the
   address).
2. **One-click session:** on any approved device row, click **Connect**
   once → Desktop tab opens with a live session (direct or relay).
   Repeat via double-click on another row, via a **Recent** chip, and
   via **Connect by code** with the box agent's code.
3. **Identity:** on the laptop (whose agent is enrolled as ESO), the
   home card shows the laptop's name, its code in large type, and
   mesh VIP 10.77.0.3.
4. **Pending approve:** enroll a throwaway device (or re-run the box
   agent with a fresh data dir) → its row shows **Approve** → one click
   → it becomes Approved and gains **Connect**.
5. **Single-instance regression:** launch the App twice → one window,
   one tray icon; close to tray → reopen from the tray → same window.
6. **Theme:** with Windows in light mode the shell renders in the light
   EsoTokens theme; in dark mode, the refined-dark variant — header,
   identity card, and rows must follow with no unreadable text.
7. **Language:** toggle العربية → home chrome, wizard, and row buttons
   render in Arabic, RTL; toggle back.
8. **Advanced regression:** wizard → **Advanced** → the original
   7-field editor opens with the current values; saving there behaves
   exactly as before.

---

## 5. Files changed (all under `src/EsoAgent.App`)

| File | Change |
|---|---|
| `App.axaml` | Merge `EsoTokens.axaml` into app resources; theme variant follows the OS (was forced Dark) |
| `MainWindow.axaml` | Token-styled shell; home column (identity card, connect-by-code, recents, search + group filter); device rows gain pin/Connect/Approve actions; window icon wiring preserved |
| `MainWindow.axaml.cs` | Home state (favourites/recents/console name), filter/search/group logic, identity resolution, one-click connect paths, code lookup, row approve/pin, first-run wizard hook; close-to-tray logic untouched |
| `DeviceItem.cs` | + `IsFavourite`/`StarText`, `CanConnect`, row action labels |
| `Strings.cs` | 27 → 82 EN/AR pairs (home + wizard) |
| `Views/DesktopView.axaml.cs` | + `StartSessionAsync()` — public name for the existing connect path (additive only) |
| `Views/PairingWizard.axaml` / `.axaml.cs` | **New** — the 3-step pairing wizard |
| `PairingSupport.cs` | **New** — fleet-URL normalization, TLS fingerprint probe + verification code, operator keygen/load, this-device matching |
| `AppSettingsStore.cs` | **New** — merge read/write helpers for `app-settings.json` + `RecentEntry` |

Untouched by design: `SingleInstance.cs`, `Program.cs`, `App.axaml.cs`,
`Themes/EsoTokens.axaml`, `Views/SettingsWindow.*` (behaviour and code),
all other views, and every non-App project.
