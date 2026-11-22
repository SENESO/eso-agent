# EsoAgent UI Overhaul — Phase 1: Audit, Gap List, Redesign Plan

**Date:** 2026-11-20 · **Status:** Audit complete; no code changed yet.
**Trigger:** Owner verdict 2026-11-20 — UI "not good, missing things, harder than
competitors, not coordinated/polished like TeamViewer."
**Method (precision design/build):** every surface inventoried from source
(XAML, HTML/JS, WinForms code), flows counted in discrete pointer actions,
competitor reference measured from TeamViewer's / AnyDesk's / RustDesk's
current documented UX. Baselines below are the numbers the redesign is
verified against in Phase 6.

**Counting convention:** one "click" = one discrete pointer action (click,
double-click, menu pick). Typing, OS file dialogs, and waiting are noted
separately. Counts start from "surface open and signed in" unless the flow
says otherwise.

---

## Phase status (updated 2026-11-22)

- **Phase 0–1 (tokens, web panels):** done — UI-PHASE01-REPORT.md.
- **Phase 2 (App shell & pairing):** done — UI-PHASE2-REPORT.md.
- **Phase 3 (session experience):** done — PHASE3-REPORT.md.
- **Phase 4 (missing-feature UIs):** done 2026-11-22 — BUILD-REPORT.md §16.
  Updates/rollout screen (fleet panel), Tasks UI (App + fleet panel +
  agent dashboard, on a new validated schedules write API), Tunnels UI
  (App manager + fleet panel live mirror), recordings browsers with
  in-browser/in-App playback (fleet panel, App, agent dashboard).
  One plan item did not survive contact with the code: **rollout cancel**
  — dispatch is synchronous, so by the time a rollout is visible every
  online device already holds the signed fetch; a cancel flag would
  misreport devices that did update. Not built; see §16.
- **Phase 5 (host side):** done 2026-11-22 — BUILD-REPORT.md §17. Host
  window (big code + copy, status, kill switch, update Apply over a new
  control-pipe action), in-app log viewer replacing the Notepad hand-off,
  consent v2 (named requester + permission rows, AR/EN), tray language
  table + toggle. F13 stays 1 click.
- **Phase 6 (i18n, a11y, verification):** pending.

---

## 1. Current-state inventory (from source)

### 1.1 Operator desktop App — `src/EsoAgent.App` (Avalonia, FluentTheme, forced Dark)

Single window, 1240×780. Header bar (logo, config-state line, version;
buttons: Open in browser / Refresh devices / العربية / Settings), device
`ListBox` on the left (330 px), 6 tabs on the right, one status line at the
bottom.

| Screen | Source | What it actually is |
|---|---|---|
| Device list | `MainWindow.axaml` | Text rows: name, detail line, ● online dot, text badges `PENDING` / `⚠ HEALTH` / `✖ HEALTH`. No search, no filter, no sort control, no groups, no favourites, no recents, no OS icon, no thumbnail. Auto-selects the first device on load. |
| Status tab | `Views/StatusView` | Buttons (Refresh / Diagnose / Apply update) + a **monospace plain-text dump** of the device status and diagnosis (`StatusView.axaml.cs` builds `key value` lines). No cards, no tables, no charts. |
| Desktop tab | `Views/DesktopView` | One wrapping toolbar row of **11 controls**: Connect, Disconnect, Control checkbox, "Quality" **numeric spinner 10–95**, Monitor combo, Paste-my-clipboard button, Record checkbox, Privacy toggle button, ●REC label. Below: state text, the screen (black border, `Stretch=Uniform`, no other scaling mode), and a **permanently docked chat block ≈110 px tall** (label + read-only log 64 px + input). |
| Terminal tab | `Views/TerminalView` | Shell combo + one-shot command line + Run button + read-only output box. Commands stream live, but each Run is an independent exec — **no persistent shell session** (no cwd/env continuity, no interactive programs, no tab completion). It is a command runner labelled "Terminal". |
| Files tab | `Views/FilesView` | **Single-pane** remote browser: path textbox, ↑ Up, Refresh; bottom row Upload… / Download… / Delete / Rename… / New folder…. Entries are one text line each (no size/date columns, no icons, no sort). Transfers report through a small state text — **no progress bar, no queue, no cancel, no drag & drop, no local pane**. Rename/New folder use a hand-rolled modal; delete confirms in the same style. |
| Activity tab | `Views/ActivityView` | Refresh + Export JSON buttons over a **monospace text dump** (NoWrap, horizontal scroll). No filter UI, no table, no search. |
| Access tab | `Views/AccessView` | Access-code minting form: 6 scope checkboxes, minutes + uses textboxes, Create; created code in a read-only box; revoke by typing a code/grant id into a textbox; grants list = **monospace text dump**. |
| Settings window | `Views/SettingsWindow` | A raw editor for the shared client config file: **7 technical fields** — Device host, Device port, **Cert fingerprint (SHA-256 hex)**, **Device token (base64)**, Fleet URL, **Fleet fingerprint (SHA-256 hex)**, **Operator key file (filesystem path)**. The config file path is displayed at the top. |
| App tray icon | `AppTray.cs` | Icon + 2-item menu (Open EsoAgent / Quit EsoAgent). Close-to-tray with a one-time explanatory dialog. Icon = fleet reachable or not. |
| First run / pairing | — | **There is no first-run or pairing flow in the App.** If `~/.esoagent/client.json` is missing/unusable, the header says "No usable client config — open Settings to create one" and the user faces the 7-field form above. In practice the config is produced by the `eso` CLI / install scripts, not by the App. |

Session facts from `DesktopView.axaml.cs`: `MaxFps = 12` hard-coded;
codecs jpeg/rawdiff; remote→local clipboard sync is automatic, local→remote
is a manual "Paste my clipboard" button; Ctrl+Alt+Del is sent only when the
operator presses the same combo locally (no on-screen button — undiscoverable);
no fullscreen; no fit/original/quality scaling switch; one device workspace at
a time (switching device re-attaches every tab).

i18n: `Strings.cs` holds **27 EN/AR string pairs** (tab names, main buttons).
Settings, Access body copy, Status output, dialogs, and the tray-hint dialog
stay English in Arabic mode. Flow direction flips; content does not fully
follow.

### 1.2 Host tray — `src/EsoAgent.Tray` (WinForms, Windows agent machine)

No window at all — a `NotifyIcon` with a right-click menu (RTL, **Arabic
hard-coded**):

- Status line (connected / standalone / locked / being-controlled / update available) — text only, disabled item.
- Device: name + code — click copies the code. Mesh IP — click copies.
- Owner session state; Last command.
- Create access code… → just opens the fleet panel in a browser.
- Open app; View log → **opens `audit.log` in Notepad**; Open dashboard (browser, `https://localhost:8737/panel/`); Lock/Resume (kill switch; resume asks owner password in an Arabic WinForms prompt); Exit.
- Icon: golden-wing brand states (gold online, grey offline, waves-lit pulsing while controlled, slate locked). This is the most polished surface in the product.

Consent prompt (`ConsentForm`): topmost fixed dialog 440×190, **English
hard-coded**, text "A remote operator is asking to CONTROL/VIEW this
computer", Allow/Deny buttons, countdown (default 30 s, no answer = No).
**It does not identify who is asking** (no operator name, no device, no
reason). No AR version.

### 1.3 Fleet server web panel — `src/EsoAgent.Server` (`Fleet.html` 787 lines + `FleetLogin.html`)

Single-page app, owner-password login (+ optional TOTP step). Header
(brand, counts, alerts badge, language toggle, Sign out), tab nav:
**Devices · Device console · Activity · Scripts · Mesh · Fleet audit · Access**,
plus a hidden device page (Status / Desktop / Files / Activity sub-tabs).

| Section | What it is |
|---|---|
| Devices | Full-width table: Code, Name (+Rename), OS, IPs, State badges, Groups (+Edit), Last seen (+health badge), actions (Open / Wake / Approve / Reject). **No search, no filter, no sort, no pagination.** Rename and Edit-groups use browser `prompt()`; Reject uses `confirm()`; Wake reports through `alert()`. |
| Device console | Device dropdown + shell dropdown + textarea + Run; quick buttons (Status/Software/Services/Processes); output in `<pre>`. **Duplicates** the device page's Status quick actions — two places to run things. |
| Device page | Status: 5 quick-action buttons → `<pre>` output. Desktop: mode/quality/monitor selects, Record checkbox, Start/Stop, Privacy toggle, canvas (backing store fixed 960×540), chat input + `<pre>` chat log. Files: path input, Open/↑Parent, upload via file input; table Name/Size/actions; Rename/Delete via `prompt()`/`confirm()`; download shows a text byte counter. Activity: `<pre>` dump. |
| Activity | Filters (device code text input, source select, since datetime) + Refresh + Export JSON/CSV; results in `<pre>` monospace text. |
| Scripts | Add/edit card (name, shell, body), Run card (pick script, **type target device codes comma-separated**), table of scripts. |
| Mesh | Device/WireGuard table; ACL rules edited as **raw text lines** (`from -> to ports`); exit-node card; enrollment-keys card. Subnet approval via `prompt()`. |
| Fleet audit | Refresh button + `<pre>` of the last 300 audit lines. No filter, no search. |
| Access | Cards: operator access revoke/restore, rotate operator key (paste base64 key), owner password change, TOTP setup/disable, alert webhook, access-code minting + grants list, server cert fingerprint. All functional; all plain. Destructive actions confirmed with `confirm()`. |

**Theme:** `:root { --bg:#0d1117; --panel:#161b22; --line:#30363d; --txt:#e6edf3; --acc:#E3B341 }` — a GitHub-dark clone. **This directly violates the
standing website law** (websites: soft, comfortable, light colours,
detail-rich hand-crafted design — not black/gold, not dark). The only brand
element is the gold wing favicon/header mark. Login page is the same dark
card; its `:focus-visible` rule references `--acc`, which that page never
defines (dead rule).

**Backend features with NO panel UI at all** (verified against
`Panel.cs` / `PanelV12.cs` routes and `OperatorApiV12.cs`):

- **Fleet update rollout** — `/panel/api/update/package`, `/panel/api/rollout*`
  exist; Fleet.html contains zero references. The fleet's headline
  maintenance feature is API/CLI-only.
- **Device scheduled tasks** — built agent-side in v1.2 (item 14); no UI in
  panel, App, or device dashboard (grep `schedul` across all three UIs: 0 hits).
- **Tunnels** — TCP/UDP tunnel streams exist; the only UI trace anywhere is a
  scope **checkbox** when minting access codes. No screen lists, starts, or
  stops a tunnel.

### 1.4 Agent-local web dashboard — `src/EsoAgent.Core/Dashboard.html` (498 lines)

Served by every agent at `https://<device>:8737/panel/` (owner password +
TOTP). Tabs: **Overview · Processes · Software · Services · Run · Desktop ·
Files · Log · Access**. Overview is real cards (System, CPU, RAM, Battery,
Uptime, per-disk free, Agent) — the best-structured page in the product.
Access tab holds the owner-only powers (pause/resume API access, token
rotation, policy toggles, password, TOTP). Same dark GitHub-clone palette
(`--bg:#0d1117`) — same website-law violation. It and the fleet panel are two
different codebases with overlapping jobs (both do Desktop, Files, Run,
Access) and visibly similar-but-not-identical layouts — the unification law
(one state, many views) is not yet reflected in one shared design.

### 1.5 Flow baselines (measured from source)

| # | Flow | Current clicks | Notes |
|---|---|---|---|
| F1 | App: first run → first device visible | **not completable in the App** | Requires CLI/install-produced config or typing host + port + SHA-256 fingerprint + base64 token (+ fleet URL + fleet fingerprint + key-file path) into Settings: 7 technical fields, 3 of them hashes/keys a human must copy from elsewhere. |
| F2 | App: open a device's live desktop | **2–3** | Device auto-selected on launch; click device (0–1) → Desktop tab (1) → Connect (1). |
| F3 | Panel: open a device's live desktop | **3** post-login | Devices → Open (1) → Desktop sub-tab (1) → Start session (1). |
| F4 | App: download one file from a device | **≥4** + OS dialog | Device (1) → Files tab (1) → double-click per folder (1 each) → Download… (1) → save dialog. No progress UI. |
| F5 | Panel: download one file | **≥3** | Open (1) → Files sub-tab (1) → Download (1). Text byte counter only. |
| F6 | App: run one command | **2** + typing | Device (1) → Terminal tab (1) → type → Enter. One-shot; no session continuity. |
| F7 | Panel: approve a pending device | **1** post-login | Devices table → Approve. (Good — keep.) |
| F8 | Panel: hand a device to a guest (access code) | **2** + typing the device code from memory | Access tab (1) → fill form → Create (1). |
| F9 | Panel: wake a device | **1** + dismiss `alert()` | Devices → Wake. |
| F10 | Panel: push an update to the fleet | **impossible in UI** | API/CLI only. |
| F11 | Any UI: schedule a task on a device | **impossible in UI** | API only. |
| F12 | Any UI: open a tunnel to a device port | **impossible in UI** | Streams exist; no management surface. |
| F13 | Host: share my code with someone | **2** | Right-click tray (1) → click Device line (1, copies code). No window, no big display. |
| F14 | Host: stop all remote access now | **2** | Right-click tray (1) → قطع الاتصال (1). (Good — keep.) |

### 1.6 Competitor reference (researched 2026-11-20)

**TeamViewer (current interface):**
- Home/Remote Support window splits the world in two: **"Receive support"**
  (your ID + session code, large type) vs **"Provide support"** (enter ID →
  Connect), with **recent connections listed on the same screen**. Connecting
  to a known device from the Devices list is a **double-click / one Connect
  button**; a January-2025 change made the toolbar Connect act on the
  selected device in one click.
- Devices view: grouped tree + searchable/filterable table, inline rename,
  per-device Connect button, columns user-configurable; global search covers
  devices, groups, **and settings/features**.
- Session window: dedicated window per session, multiple sessions as tabs;
  slim top toolbar — View (scaling: original / scaled / full-screen, quality
  Auto/Optimize speed/Optimize quality, resolution), Actions (Ctrl+Alt+Del
  as a button, reboot, lock), Files & Extras (file transfer opens a
  **dual-pane manager**, also reachable by drag & drop onto the session),
  chat panel that **slides in on demand** instead of permanently docking,
  session info (connection quality), record, annotate/whiteboard.
- File transfer is also a first-class connect mode (pick "File transfer"
  instead of "Remote control" before connecting) and files can be **sent to
  a device from the list without any session** (paperclip action).
- Settings: one Options dialog with a left category tree (General, Security,
  Remote Control, Meeting, Computers & Contacts, Audio, Video, Custom
  modules, Advanced) — raw internals (hashes, key paths) never appear;
  identity is an account sign-in, not pasted fingerprints.
- TeamViewer ONE restates the same lesson at suite level: top-level sections
  named after jobs (Manage / Resolve / Automate / Measure) and a **unified
  device view** — one page per device with alerts, patch, health together.

**AnyDesk:** main window = your address (large, copyable) + one remote-address
field + Connect; address book tiles with recents/favourites; the **accept
window shows per-session permission toggles** (keyboard/mouse, clipboard,
sound, file transfer) before Accept; session toolbar is a slim auto-hiding
bar. **RustDesk** copies this deliberately: own ID + one-time password on the
left, remote ID + Connect / Transfer-files buttons on the right, **no account
required**, two-pane file transfer, favourites + recent sessions on the home
screen. The shared pattern: *the home screen is the connect flow* — ID,
recents, and one action; everything else is behind it.

---

## 2. Gap list

Severity: **S1** blocks or misleads a core job · **S2** core job noticeably
harder/uglier than competitors · **S3** polish/coherence.

| # | Surface / flow | TeamViewer-class behaviour | EsoAgent today | Gap | Sev |
|---|---|---|---|---|---|
| G1 | App first run / pairing (F1) | Sign in with account, or type partner ID + password; devices appear | No flow exists; 7-field raw config editor incl. two SHA-256 fingerprints, a base64 token, a key-file path | The first thing a new user meets is the hardest screen in the product | **S1** |
| G2 | Fleet panel + agent dashboard + both logins | Branded, calm product theme | GitHub-dark clone `#0d1117`; violates the owner's soft-light website law | Wrong theme family on every web surface; zero product identity beyond the wing mark | **S1** |
| G3 | Fleet rollout / updates (F10) | Managed-device update/patch is a first-class screen (ONE: Manage → patch status) | API + panel routes exist; **zero UI** | A headline fleet feature is invisible to the owner | **S1** |
| G4 | Scheduled tasks (F11) | Automations/Scripts is a top-level section (TV ONE "Automate") | Backend built (v1.2 item 14); **zero UI anywhere** | Built capability unreachable without the API | **S1** |
| G5 | Tunnels (F12) | VPN/port-forward presented as a connect mode with state | Streams exist; only a scope checkbox mentions tunnels | No list/start/stop/state anywhere | **S2** |
| G6 | App Status / Activity / Access; panel Activity / Audit / console output | Cards, tables, filters, human timestamps | Monospace text dumps in read-only boxes / `<pre>` | Data the product already has is presented as raw logs; unscannable beyond ~20 lines | **S1** |
| G7 | Desktop session (App + panel) | Slim toolbar: fullscreen, scaling modes, named quality presets, CAD **button**, session stats, on-demand chat panel | 11 mixed controls in one row; numeric quality spinner 10–95; `MaxFps=12` hard cap; no fullscreen; no scaling choice; CAD only via local key combo; chat permanently docked ≈110 px (App) | The core session feels like a debug harness next to any competitor | **S1** |
| G8 | File transfer (F4/F5) | Dual-pane manager, drag & drop both ways, transfer queue with per-file progress, send-without-session | Single remote pane, button-per-action, state-text feedback, no queue/progress/cancel, no drag & drop | Slowest-feeling flow in the product; large transfers are blind | **S1** |
| G9 | Panel dialogs | Product dialogs with context and validation | Browser `prompt()` (rename, groups, subnets), `confirm()` (reject, delete, revoke, rotate), `alert()` (wake result) | The single clearest "unpolished" tell; also blocks rich input (multi-select groups, CIDR validation) | **S2** |
| G10 | Device list (App + panel) | Search, filter, sort, groups tree, favourites, recents, inline rename | No search/filter/sort anywhere; groups = comma text in a `prompt()`; App list is bare text rows | Finding one device in a real fleet is O(n) eyeballing | **S2** |
| G11 | Language coherence | One language strategy per surface, complete | Tray menu Arabic-only; consent prompt English-only; panel i18n covers nav + some headings only (Access/Mesh/Scripts bodies hard-coded English); App translates 27 strings, Settings/Access/Status stay English | An Arabic-first owner gets a different product per surface; the person at the device gets English only | **S2** |
| G12 | Consent prompt (host) | Accept window names the requester + shows permission toggles (AnyDesk) | "A remote operator is asking…" — anonymous; fixed Allow/Deny; English only | The trust moment of the whole product is anonymous and foreign-language | **S2** |
| G13 | Panel information architecture | Job-named sections; one unified device page | "Device console" tab duplicates the device page's job; Scripts targets typed as raw codes; Mesh mixes ACL text editing with key management; no fleet Overview home (header counts only) | Same job in two places; raw codes/text where pickers belong | **S2** |
| G14 | Terminal (App) | (SSH-class tools) persistent interactive session, history, reconnect | One-shot command runner; no cwd continuity, no history recall UI, no session state | Name promises a terminal; behaviour is a run-box | **S2** |
| G15 | Sessions model (App) | One window/tab per session; several devices at once | Single workspace; switching device re-attaches all tabs; no session list | Can't watch two machines side by side | **S2** |
| G16 | Host surface | Home window: big own-ID, copy buttons, session state, history (TV/RustDesk home) | Tray menu only; audit log opens in Notepad; "update available" is status text with no action | The host side has no face; the owner can't *show* anyone their code | **S2** |
| G17 | Recordings | Browsable, playable session library | `.esorec` files listed as a heading in the device dashboard Files tab; no browser/player UI in App or fleet panel | Recordings exist but are effectively unwatchable | **S3** |
| G18 | Settings (App) | Category tree; safe defaults up front, Advanced folded away | One flat grid = the config file, path printed on top; cert/token fields are the *primary* UI | Settings *is* the pairing problem (G1) in a second costume | **S2** |
| G19 | Panel desktop canvas | Responsive canvas, scaling options, fullscreen | Backing store fixed at 960×540 attributes, CSS-stretched; no fullscreen/scaling controls | Blurry or letterboxed sessions on hi-dpi monitors | **S3** |
| G20 | Micro-polish set | Empty states, loading states, relative timestamps, human sizes | None: empty tables are blank; times are raw `2026-11-19 08:31:22` strings; file sizes are raw bytes in panel device files; FleetLogin `:focus-visible` references an undefined `--acc` variable | Death by a thousand cuts — the owner's "not coordinated" in concrete form | **S3** |

**Coverage check:** of the 46 v1.2 feature items, the ones with backend but
no (or checkbox-only) UI are: rollout/updates (G3), scheduled tasks (G4),
tunnels (G5), session recordings playback (G17). Everything else has at
least a functional — if unpolished — surface.

---

## 3. Redesign plan

### 3.1 Design decisions (fixed before any screen work)

1. **One token set, two themes.** A single design-token file (spacing, type,
   radii, semantic colours, badge states) consumed by the panel, the agent
   dashboard, and the App, so all three faces finally match.
2. **Web = soft light (owner's law).** Panel, agent dashboard, and both login
   pages go light: warm paper background, white surfaces, gold used as an
   *accent* (primary actions, active states, the wing mark) — never as a
   black/gold theme.
3. **App = refined dark, same tokens.** The desktop App keeps the product's
   matte-dark identity (gold wing on matte black is the brand) but rebuilt on
   the shared tokens: same spacing scale, same badges, same type ramp as the
   panel's dark variant; a light variant ships behind the same tokens and
   follows the OS by default. (Owner-flippable in one token switch.)
4. **No native dialogs, no text dumps.** Every `prompt()/confirm()/alert()`
   and every monospace data dump is replaced by a product dialog or a real
   table/card component. Zero exceptions in the acceptance pass.
5. **The home screen is the connect flow** (TeamViewer/RustDesk pattern):
   devices + recents + favourites + one primary action per device.
6. **One device page everywhere.** App, fleet panel, and agent dashboard
   present the same device page structure (Overview / Desktop / Terminal /
   Files / Tasks / Activity) fed by the same state — the unification law made
   visible. The fleet panel adds fleet-only sections (Overview, Scripts,
   Updates, Mesh, Access, Audit); the agent dashboard keeps its owner-only
   Access powers inside the same frame.

### 3.2 Visual system

**Tokens (panel, light):**

| Token | Value | Use |
|---|---|---|
| `--bg` | `#F7F5F1` | page background (warm paper) |
| `--surface` | `#FFFFFF` | cards, tables, dialogs |
| `--surface-2` | `#FBFAF7` | nested/hover rows |
| `--line` | `#E6E1D6` | borders, dividers |
| `--txt` | `#20242E` | primary text |
| `--mut` | `#6E7480` | secondary text |
| `--acc` | `#B8860B`-family gold, text-safe `#8A6508` | links, active states |
| `--acc-solid` | `#E3B341` | primary button fill (with `#1A1A1A` text) |
| `--ok` / `--warn` / `--bad` | `#2F9E44` / `#E08A00` / `#D64545` | state semantics (badges pair colour + label, never colour alone) |

**App (dark variant):** `--bg #131519`, `--surface #1B1E24`, `--line #2C3038`,
`--txt #ECEEF2`, `--mut #9BA1AC`, same gold + semantics.

**Type:** system UI stack both platforms; scale 12 / 13 / 14 (base) / 16 / 20 /
24; tabular numerals for codes, times, sizes. **Spacing:** 4-pt scale
(4/8/12/16/24/32). **Radii:** 8 controls, 12 cards, 999 badges. **Elevation:**
one soft shadow level for dialogs/menus only. **Motion:** 120–150 ms
border/background transitions (already the panel's one good habit — keep).
**Contrast gate:** every text/background pair ≥ 4.5:1, verified in Phase 6.
**Density:** device rows 36 px (panel) / 44 px (App, with OS icon + status).

### 3.3 Target information architecture

**Operator App** — left icon rail + content:
`Devices` (home: search, group filter chips, favourites, recents, device
cards/rows with per-device **Connect** button and overflow menu) ·
`Sessions` (live sessions, switch/close) · `Activity` (unified timeline,
filterable table) · `Access` (codes & grants as tables) · `Settings`
(categorised: Connection / Session / Language & appearance / Advanced —
raw config fields live only under Advanced, behind "show technical details").
Selecting a device opens its workspace: `Overview · Desktop · Terminal ·
Files · Tasks · Tunnels · Activity`. First run replaces all of this with the
**pairing wizard** (§3.4 F1).

**Fleet panel** — top bar + sections:
`Overview` (fleet health cards: online/offline/pending counts, alerts 24 h,
health failures, recent activity — the data already fetched for the header) ·
`Devices` (searchable/sortable table, group filter, inline rename dialog,
bulk select) · device page (same six sub-views as the App) · `Activity` ·
`Scripts` (targets via device multi-picker) · `Tasks` (scheduled tasks) ·
`Updates` (package + rollout with per-device progress) · `Mesh` · `Access` ·
`Audit` (filterable table). The standalone "Device console" tab is **deleted**
— its job moves to the device page (Terminal sub-view), ending the G13
duplication.

**Agent dashboard** — same frame and tokens as the fleet panel, device page
only, plus its owner-only Access cards. Shared CSS/JS components with the
fleet panel (one codebase for the device page).

**Host tray** — menu stays (it is genuinely quick), plus a **Host window**:
giant device code with Copy button, mesh address, status, kill-switch
toggle, current/last session, "open audit log" opens an in-app log viewer
(not Notepad), update-available becomes a button that applies the update.

### 3.4 Screen-by-screen spec with target click counts

| Flow | Spec (key changes) | Current | Target |
|---|---|---|---|
| F1 Pairing | Wizard, 3 steps: (1) choose "Join a fleet" or "Direct device"; (2) fleet: paste **one enrollment string** (or enter fleet URL + the device code shown on the host) — fingerprints/tokens resolved automatically over the pinned channel, shown as a single "verify this code matches" comparison, never typed; direct: host + the host's pairing code; (3) name this computer → Done. Fallback "enter details manually" link opens today's form under Advanced. | not completable (7 fields) | **3 steps, 0 hashes typed** |
| F2 App desktop connect | Device row/card carries a primary **Connect** button; double-click/Enter on a device also connects; session opens in its own window. | 2–3 | **1** |
| F3 Panel desktop connect | Device page defaults to Overview with a primary **Desktop** CTA; choosing Desktop starts the session (no separate Start click; Stop in the session bar). | 3 | **2** |
| F4/F5 File transfer | **Dual-pane** manager (local ↔ remote) in App device workspace and panel device page: drag & drop both directions, multi-select, transfer queue dock with per-file progress bar, speed, cancel, and completed state; sizes/dates as columns; send-to-device from the device list overflow menu without opening a session. | ≥4 / ≥3, blind | **≤2 + drag** |
| F6 Terminal | Real persistent session per device (shell stays alive across commands; cwd/env persist), command history (↑/↓), multiple terminal tabs per device, reconnect banner. Rename nothing — make it true. | 2, one-shot | **2, persistent** |
| F7 Approve device | Unchanged (already 1 click); add the same Approve action to the App device list for pending devices. | 1 | **1** |
| F8 Access code | Device page → Access panel: device pre-filled from context; scope as labelled toggle rows with plain-language descriptions; created code shown in a dialog with Copy button + expiry summary; grants as a table with Revoke buttons. | 2 + typed code | **2, 0 typed codes** |
| F10 Fleet update | New **Updates** section: current package card (version, size, signature state), upload/replace package, rollout creator (target picker: all / group / selected devices), rollout progress table (per device: pending → downloading → applied → verified / failed with reason), cancel rollout. | impossible | **3 to launch** (pick package → pick targets → Start) |
| F11 Scheduled tasks | Device page **Tasks** sub-view: table (name, schedule in human words, command/script, last run + result, enabled toggle), New/Edit dialog with schedule builder (daily/weekly/interval presets + cron under Advanced), Run-now. | impossible | **2 to create form** |
| F12 Tunnels | Device page **Tunnels** sub-view: table of tunnels (local port → device host:port, TCP/UDP, state, traffic), New tunnel dialog, Start/Stop; same list mirrored read-only in the panel. | impossible | **2 to create form** |
| F13 Host share code | Host window: code at 32 px with Copy; tray menu keeps its copy item. | 2 | **1** (window) / 2 (tray, unchanged) |
| Session window (App) | Slim two-row chrome: row 1 — device name + connection state + session stats (fps, latency ms, codec) + Record + Privacy; row 2 (collapsible, auto-hides in fullscreen) — View menu (Fit / Original / Fullscreen F11), Quality segmented preset (Speed / Balanced / Quality / Auto — maps to the existing numeric range), Monitor switcher, **Send Ctrl+Alt+Del button**, Clipboard sync toggle (auto both ways), Files (opens transfer), Chat toggle (slide-over panel, unread dot). Esc closes fullscreen, not the session. | 11-control row, chat docked | all session actions ≤1 click from the bar |
| Panel desktop | Canvas becomes responsive (backing store = displayed size × devicePixelRatio), same View/Quality/CAD controls as the App session bar, chat as slide-over. | fixed 960×540 store | parity with App bar |
| Status/Overview (App + panel device page) | Cards (System, CPU, RAM, Battery, Uptime, Disks with free-space bars, Network/mesh, Agent) — port the agent dashboard's existing Overview cards to both surfaces; Diagnose renders as a checklist table (status icon, check, detail), not text. | monospace dump | — |
| Activity / Audit | Real tables: time (relative + absolute on hover), source badge, device, kind, summary, result badge; filters as controls; export stays. | `<pre>` dump | — |
| Settings (App) | Categories left, forms right; Connection shows fleet name + status + "re-pair" instead of raw fields; Session holds quality/fps defaults; Advanced reveals today's 7 fields unchanged (nothing is lost, it is demoted). | flat 7-field grid | — |
| Consent prompt (host) | Names the requester ("Eslam's App / fleet panel"), shows requested permissions as icons (view/control, clipboard, files), AR + EN (host's language), Allow once / Deny, countdown retained. | anonymous, EN only | — |
| Login pages | Light theme per §3.2, brand mark, error banner component (also fixes the undefined `--acc` focus rule). | dark, dead CSS var | — |

### 3.5 What to add / remove / simplify

**Add (UI for existing capability):** Updates/rollout screen (G3);
scheduled-tasks UI (G4); tunnels UI (G5); recordings browser with playback
or at minimum open/download (G17); fleet Overview home; device search /
filter / sort / favourites / recents (G10); pairing wizard (G1); host window
(G16); session stats + fullscreen + scaling (G7); transfer queue (G8);
requester-named consent (G12).

**Add (net-new, competitor table stakes):** send-file-to-device without a
session (device list action); global search in the panel (devices + sections);
bulk device actions (approve/rename-group/update on a selection).

**Remove:** the panel's "Device console" tab (duplication, G13); the App's
permanently docked chat block; raw ACL text editing (replaced by a rule
table with add/remove rows; raw text stays under an Advanced toggle);
comma-code script targets (replaced by a device multi-picker).

**Simplify:** Settings 7 fields → 1 pairing state + Advanced (G18);
quality spinner → 4 named presets (G7); access-code scopes → labelled
toggle rows with descriptions (both App and panel); mesh subnet approval
`prompt()` → dialog with the advertised subnets as checkboxes (G9).

### 3.6 Phase breakdown (implementation order)

| Phase | Scope | Exit criteria (measured) |
|---|---|---|
| **0 — Tokens & components** | Token file (light panel + dark app variants), shared CSS for panel + dashboard, Avalonia resource dictionary for the App; component set: button, badge, card, table, dialog, toast, empty state. No screen changes yet. | Tokens compile into all three surfaces; contrast audit ≥ 4.5:1 on every text pair (scripted check). |
| **1 — Web panels** | Fleet panel + agent dashboard + both logins rebuilt on light tokens; all `prompt/confirm/alert` replaced by dialogs; Devices table gains search/filter/sort; Activity/Audit become tables; fleet Overview home added; console tab removed. | `grep -c "prompt(\|confirm(\|alert("` in panel JS = 0; no `#0d1117` anywhere; F3 = 2 clicks; F7 stays 1. |
| **2 — App shell & pairing** | Pairing wizard (F1); device home (search, groups, favourites, recents, Connect buttons, pending-approve); Settings categorised with Advanced demotion; Status/Activity/Access rebuilt as cards/tables. | F1 = 3 steps / 0 typed hashes (timed walkthrough); F2 = 1 click; zero monospace data dumps in the App. |
| **3 — Session experience** | Session window chrome (fullscreen, scaling modes, named quality presets, CAD button, stats, chat slide-over); fps ceiling raised from 12 with an adaptive default; dual-pane Files with drag & drop + transfer queue (App + panel device page); persistent terminal sessions. | F4 = ≤2 clicks + drag; every session action ≤1 click from the session bar; transfer of a 100 MB file shows live progress + cancel works. |
| **4 — Missing-feature UIs** | Updates/rollout screen; Tasks (scheduled) UI; Tunnels UI; recordings browser. App + panel parity per §3.3. | F10 = 3 clicks; F11/F12 forms reachable in 2 clicks; every v1.2 backend feature has a UI home (checklist against FEATURE-MATRIX). |
| **5 — Host side** | Host window (big code, copy, status, kill switch, update action); in-app log viewer replaces Notepad; consent prompt v2 (named requester, permission icons, AR/EN). | F13 = 1 click; consent shows requester identity in both languages. |
| **6 — i18n, a11y, verification** | Complete AR string coverage on all surfaces (0 hard-coded user-visible strings in tray/consent/panel bodies); keyboard pass (tab order, F11/Esc, mnemonics); re-measure every F-flow against §1.5 baselines and §3.4 targets; screenshot set for owner verdict. | Click-count table re-measured and inside targets; AR mode shows 0 English body strings outside the technical-terms allowlist; owner verdict requested on captures. |

Phases are independently shippable; 0→1→2 is the critical path for the
owner's verdict (the three surfaces he actually sees), 3 is the session
feel, 4 closes the "missing things" list, 5–6 finish the host side and the
proof pass.

---

## Appendix — evidence index

- App shell/tabs: `src/EsoAgent.App/MainWindow.axaml`, `MainWindow.axaml.cs`
- App views: `src/EsoAgent.App/Views/*.axaml(.cs)`; i18n table:
  `src/EsoAgent.App/Strings.cs` (27 pairs); config form:
  `Views/SettingsWindow.axaml`; config model: `AppConfig.cs` (7 fields).
- Session limits: `Views/DesktopView.axaml.cs` (`MaxFps = 12`, codecs,
  clipboard, CAD-on-local-combo).
- Host tray + consent: `src/EsoAgent.Tray/TrayContext.cs` (menu, Notepad log,
  anonymous English `ConsentForm`).
- Fleet panel: `src/EsoAgent.Server/Fleet.html` (theme line 9 of `<style>`,
  `prompt/confirm/alert` in device/mesh/access handlers), `FleetLogin.html`
  (undefined `--acc` in `:focus-visible`), routes: `Panel.cs`, `PanelV12.cs`
  (rollout/update routes with no UI), `OperatorApiV12.cs` (tunnel streams).
- Agent dashboard: `src/EsoAgent.Core/Dashboard.html` (Overview cards; dark
  palette), `Dashboard.cs`.
- Feature truth source: `FEATURE-MATRIX.md`, `V12-REPORT.md` (item 14
  scheduled tasks built; tunnels items 5/24; rollout in v1.2 set).
- Competitor references: TeamViewer Remote Support window + Devices view +
  session toolbar documentation and 2025 release notes (one-click Connect,
  inline rename, global search); TeamViewer ONE UI updates (Manage/Resolve/
  Automate/Measure, unified device view); AnyDesk accept-window permission
  toggles; RustDesk home layout (own ID left, remote ID + Connect/Transfer
  right, no account).
