# EsoAgent UI Overhaul — Phase 3 Report (Session experience)

**Date:** 2026-11-20 · **Scope:** Phase 3 from `UI-OVERHAUL.md` §3.6, as
scoped by the parent task: (1) session window chrome in the App's
Desktop view, (2) the fps ceiling, (3) dual-pane Files with drag & drop
+ transfer queue, (4) persistent terminal sessions.
**Status:** implemented, built warning-free, test suite green against
baseline; committed locally — **push pending ESO** (the laptop was
unreachable for the whole session: SSH answers "Connection closed by
UNKNOWN port 65535", the device-off/asleep signature; per instructions
no other push route was forced). No version bump, no release — this
ships batched in the next release after the v1.2.7 acceptance round.

**Commits (local clone `~/workspace/.eso-git/eso-agent`, on top of
`8a1920b`):** feature commit `404b0b7` (code + tests +
CHANGELOG), followed by a second commit adding this report. The parent
pushes both via the staged clone on ESO + one-time schtasks /it, the
usual route.

---

## 1. What was built

### 1.1 Session chrome (Desktop view)

The 11-control wrap row is now a two-row session bar; every action is
one click (or one menu pick, the audit's counting convention) from it:

- **Row 1 — connection & session state:** Connect / Disconnect, Control
  checkbox, Record checkbox + ●REC, Privacy toggle, **Chat toggle with
  an unread-count badge**, **Fullscreen button**, and the **live stats
  readout**.
- **Row 2 — view & actions:** scaling-mode combo, quality-preset combo,
  monitor combo, **Send Ctrl+Alt+Del button** (the combo no longer
  requires pressing the same keys locally), Paste-my-clipboard.
- **Fullscreen:** button or **F11** toggles; the view raises
  `FullscreenChanged` and the shell (MainWindow) owns the window state
  and hides its own chrome (header bar, home column, status bar). Esc
  closes the chat slide-over first, then leaves fullscreen in View
  mode; in Control mode Esc belongs to the remote machine and is
  forwarded. The tab strip remains visible in fullscreen (the one
  chrome element the shell cannot hide without re-parenting the view)
  — stated plainly rather than hidden.
- **Scaling modes:** Fit (Uniform, the old behaviour) / Actual size
  (native pixels, centered) / Stretch (Fill). Purely local — no
  reconnect — and the pointer normalization is mode-aware (letterbox
  math for Fit, centered-native for Actual, direct for Stretch), so
  clicks land where they look in every mode.
- **Named quality presets** replace the 10–95 numeric spinner:
  Auto (60, 30) · Balanced (60, 20) · Speed (40, 30) · Quality (88, 15)
  — quality + MaxFps pairs defined once in Core (`DesktopPresets`,
  unit-tested) and mapped into the existing hello. Like every stream
  parameter they are hello-time: changing one mid-session reconnects,
  the honest behaviour the view already had for quality/monitor.
- **Stats:** `DesktopStatsTracker` (Core, unit-tested) fed by every
  received frame; the bar shows `fps · WxH · KB/frame · KB/s` from a
  2 s sliding window, refreshed at 2 Hz. **Latency is not shown**: the
  desktop protocol carries no timestamps, so there is no honest RTT —
  nothing was invented.
- **Chat slide-over:** the v1.2 in-session chat (DesktopChat — the
  protocol already carried it) moves from the permanently docked
  ≈110 px block to a 320 px token-surfaced slide-over on the right,
  toggled from the bar; messages arriving while it is closed increment
  the unread badge.

### 1.2 FPS ceiling

The App's hard-coded `MaxFps = 12` was the only 12 in the system: every
agent capture path already clamps to **1..30** (`DesktopStreamRunner`,
the Windows tray handler, the Linux helper), the bench client already
requests 30, and `AdaptiveQuality` already tunes the real rate/quality
down from the requested ceiling to the measured link (floor: quality
40 / 4 fps). The presets therefore request up to the proven protocol
ceiling of 30, and the default preset is Auto — the adaptive path.
What the sandbox can measure is this VM's scrot capture path
(BENCHMARKS.md: 1.8–2.6 fps on an idle Linux desktop), which is a
capture-path figure, not a protocol ceiling; the sustained rate on the
owner's Windows/GXDI path is a live-verification item (§4).

### 1.3 Files — dual pane + transfer queue

- **Two panes:** this computer (System.IO browser: path box, Up,
  double-click navigation) on the left, the device (the existing
  `/api/files` listing) on the right, device housekeeping
  (Delete / Rename / New folder) unchanged under the remote pane.
- **Transfers:** select in one pane → **Upload / Download** button
  (one click), **drag & drop** between the panes (Avalonia 11.3
  DataTransfer API, 6 px drag threshold so clicks still select), drop
  OS files onto the device pane to upload them, or double-click a file
  to send it across. F4 = Files tab → select → 1 click, or one drag.
- **Queue:** all transfers run through Core's new `TransferQueue`
  (sequential, in order) + `TransferEngine` — the begin → chunk →
  SHA-256 commit upload and info → ranged download loops the view used
  to inline, now written once against a narrow `ITransferChannel` seam
  (the App adapts its `Transport`; the wire protocol is byte-identical,
  including the fleet 256 KB / direct 4 MB chunk sizes). The dock shows
  per-file progress bar, bytes, smoothed speed, state, and a **cancel
  button**: cancellation is cooperative at a chunk boundary (the
  Transport carries no token; an in-flight chunk completes, the next
  never starts), a cancelled upload's partial stays on the device and
  the next attempt resumes it via the protocol's begin offset, and a
  cancelled download leaves its partial local file — nothing is ever
  committed or hash-verified as complete after a cancel.
- **Panel device page: not done, deliberately.** The task conditioned
  it on the backend exposing transfer state; it does not — transfers
  are stateless request/response chunks (the panel proxies them from
  the browser, PanelV12), there is no transfer registry or progress
  endpoint to surface. The queue is App-side state, exactly like the
  panel's own browser-side byte counter. Noted here rather than faked.

### 1.4 Persistent terminal sessions

The agent has hosted **persistent shell sessions since v1.2**
(`ShellSessionService`: named interactive shells that survive client
disconnects, output buffered with sequence numbers, plain
request/response API — `/api/sessions` — that works identically direct
and via the fleet relay, already driven by the CLI). The App never used
them; its Terminal tab was a one-shot runner. Now:

- The tab creates (or adopts) a per-device session named `app` on
  first attach, polls `GET /api/sessions/{name}?since=<seq>` at
  700 ms, and posts input lines; the shell's cwd/environment persist
  because the shell itself persists on the device.
- App-side attach state (session name, shell, output buffer, history)
  lives in `TerminalSessionStore`, keyed by device and kept for the
  app's lifetime: switching devices or tabs unbinds the view only;
  returning reattaches and catches up from the last seen sequence.
  Catch-up correctness (dedupe of replayed overlap, out-of-order
  batches, agent-side ring eviction gaps, bounded trimming on line
  boundaries) is Core's `ShellSessionBuffer`, unit-tested.
- ↑/↓ command history per session; End session and Restart actions on
  the bar; a shell change applies on Restart.
- Devices whose agent has no session service fall back to the previous
  one-shot runner verbatim (direct streamed exec / fleet POST
  /api/shell), detected by create+list both failing — the tab never
  regresses below what it was.

### 1.5 Strings / theme discipline

All new user-visible text goes through `Strings` (EN/AR pairs added:
session chrome, presets, scaling, transfers, terminal session states).
New surfaces use the EsoTokens brushes (chat slide-over, queue dock);
no new hard-coded dark surfaces, no `prompt()`/`alert()` anywhere (the
web assets were not touched; their Phase 1 counts remain 0).

---

## 2. Files changed (16 + this report)

| File | Change |
|---|---|
| `src/EsoAgent.Core/DesktopProtocol.cs` | + `DesktopQualityPreset` + `DesktopPresets` (mapping, ceiling, parse) |
| `src/EsoAgent.Core/SessionSupport.cs` | **New** — `DesktopStatsTracker`, `ShellSessionBuffer` |
| `src/EsoAgent.Core/TransferEngine.cs` | **New** — `ITransferChannel`/`TransferReply`, `TransferJob`, `TransferEngine`, `TransferQueue` |
| `src/EsoAgent.App/Services/TransportChannelAdapter.cs` | **New** — Transport → `ITransferChannel` |
| `src/EsoAgent.App/Services/TerminalSessions.cs` | **New** — per-device terminal attach state + store |
| `src/EsoAgent.App/Views/DesktopView.axaml(.cs)` | Two-row session bar, presets, scaling modes, stats, CAD button, fullscreen, chat slide-over + unread badge, mode-aware pointer mapping, `ApplyLanguage`, `FullscreenChanged` |
| `src/EsoAgent.App/Views/FilesView.axaml(.cs)` | Dual panes, drag & drop (DataTransfer API), transfer queue dock + view models, pane-driven Upload/Download; remote ops unchanged |
| `src/EsoAgent.App/Views/TerminalView.axaml(.cs)` | Persistent-session attach client (create/adopt, poll, input, history, End/Restart) + one-shot fallback preserved |
| `src/EsoAgent.App/MainWindow.axaml(.cs)` | `HeaderBar`/`HomePanel` names; `FullscreenChanged` handler (window state + chrome visibility); `ApplyLanguage` reaches Desktop/Terminal tabs; window close also detaches the terminal poller |
| `src/EsoAgent.App/Strings.cs` | + 25 EN/AR pairs (Phase 3) |
| `tests/EsoAgent.Tests/Phase3Tests.cs` | **New** — 13 tests (§3) |
| `CHANGELOG.md` | `## [Unreleased]` section at the top |

No Core protocol, endpoint, or server file was changed; no API or
payload changed anywhere. The desktop hello gains nothing new — presets
only choose values for the existing `Quality`/`MaxFps` fields.

---

## 3. Verification (measured here)

| Check | Result |
|---|---|
| `dotnet build EsoAgent.sln` (full) | **0 warnings, 0 errors** (the Avalonia 11.3 drag-drop work uses the current `DataTransfer` API specifically to keep this at zero) |
| Test suite (xunit console runner, sandbox) | **Total 230, Passed 226, Failed 4** — the 4 failures are name-for-name the pre-change baseline (`RelayDesktopTests` ×2, `PackageUploadLimitTests`, `EfficiencyTests.PackageVersion_StampRoundTrip`); baseline before: 217/213/4 |
| New tests (13, all pass) | Presets: mapping pinned, ranges inside agent-enforced limits, default = Auto at the 30 ceiling, parse round-trip. Stats: 21 frames @50 ms → 20.0 fps / exact throughput; window expiry. Shell buffer: ordered append, reattach dedupe, out-of-order sort, gap acceptance, line-boundary trim. Transfers (below) |

**Transfer test seam, stated honestly:** the engine tests drive the
real `TransferEngine`/`TransferQueue` through an in-process
`ITransferChannel` whose handler calls the **real**
`FileTransferService` (the same service the agent's `/api/files`
endpoints wrap) rooted at a temp dir — so the begin → chunk → commit
and info → ranged-download sequences, resume offsets, and hash checks
are the production code paths; only HTTP itself is fake. Against that
seam: a **100 MB upload** completes with monotonic progress ending at
exactly 100,000,000 bytes and a hash-verified commit; a 3 MB
upload→download round-trips byte-for-byte; **cancel mid-upload** (held
deterministically at the second chunk) ends Cancelled with the target
never committed and the partial holding exactly the 2 stored chunks
(8 MB, resumable); two queued jobs run sequentially in enqueue order;
an upload resumes from a pre-seeded 4 MB server-side partial.

## 4. Not verified here (needs a live session on the owner's machine)

- The App cannot be launched in this sandbox (no display): the session
  bar layout, fullscreen enter/exit, scaling-mode pointer accuracy,
  drag & drop feel, and the chat slide-over are compile-verified only.
- A real 100 MB transfer over the wire (direct + fleet relay) with the
  progress dock watched live, and cancel pressed mid-flight.
- Sustained desktop fps on the Windows DXGI capture path at the Auto
  preset (the number BENCHMARKS.md's VM figures cannot speak for).
- Terminal: a live session's cwd persistence across commands
  (`cd` then `pwd`), reattach after switching devices mid-output, and
  the fallback path against a pre-v1.2 agent.
- F4 / session-bar click counts re-measured live against §1 targets
  (the Phase 6 pass owns the formal re-measurement).

## 5. Exit criteria vs the plan (§3.6 Phase 3)

| Criterion | State |
|---|---|
| F4 = ≤2 clicks + drag | By construction: Files tab → select → Upload/Download (1 click) or one drag or double-click; live re-measurement pending (§4) |
| Every session action ≤1 click from the session bar | Yes — all controls are direct bar buttons/combos; fullscreen also on F11 |
| 100 MB transfer shows live progress + cancel works | Proven at the engine/queue seam (§3); live-wire proof pending (§4) |
| fps ceiling raised from 12 with an adaptive default | Yes — 12 is gone from the App; Auto = (60, 30) with agent-side adaptation |
