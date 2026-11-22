# Changelog

All notable changes to EsoAgent are documented here. The project follows
semantic versioning; the version lives in one place (`Directory.Build.props`)
and is stamped on every surface — agent, fleet server, client, and app.

## [1.2.8] — 2026-11-22

Two field defects from the mesh live-verification rounds, both in the
"silently dead until somebody restarts something" class:

- **Relay shim loops no longer die on a receive error.** The shim's
  relay loop delivers to the WireGuard engine's port over the same
  loopback socket the WG loop receives on; a delivery landing before
  the engine's bind earned the socket an ICMP Port Unreachable, the
  WG loop's pending receive threw WSAECONNRESET, and the loop exited
  for good — outbound relay forwarding stopped silently until a
  quiet-window restart. The loops now disable SIO_UDP_CONNRESET on
  Windows, treat a receive error as a lost packet instead of a death,
  and are restarted in place by the shim sync if one ever does exit.
- **The fleet server re-pushes state frames on a timer.** The
  connect-time state frame was the only one a device ever received;
  when the server woke from hibernation it accepted a device's
  WebSocket but the frame never arrived, and the device looped on its
  60 s no-state-frame self-heal until the server was restarted by
  hand. Every 30 s the server now re-pushes the enrollment state
  frame (and the mesh frame for approved devices) to every connected
  device, so a lost frame heals on the next tick.
- **A fresh install joins the fleet by itself.** The fleet hub ships
  pre-configured — as the default in the agent's fleet options and in
  the config both installers seed — so on first run the agent registers
  and the app on that machine shows its device code with zero setup.
  The device lands as Pending; approval still happens in the panel
  before any command is relayed. A different hub (or none) is one
  `fleet` section in esoagent.json away.
- Also in this release: the mesh-handshake fix from the 1.2.7 field
  rounds (relay shims sync before the engine is configured, shim sync
  is idempotent, a dead engine UDP receiver is recovered) and the
  published benchmark suite (BENCHMARKS.md).

## [Unreleased]

UI overhaul Phase 3 — the session experience in the desktop app:

- **Session window chrome** — the desktop tab's toolbar is now a two-row
  session bar where every action is one click: fullscreen (button or
  F11; the shell hides its own chrome), scaling modes (Fit / Actual size
  / Stretch, applied locally with pointer mapping to match), named
  quality presets (Auto / Balanced / Speed / Quality) instead of the raw
  numeric spinner, a Send Ctrl+Alt+Del button, live session stats
  measured from frame arrivals (fps, resolution, frame size,
  throughput), and chat as a slide-over panel with an unread badge
  instead of a permanently docked block.
- **Frame-rate ceiling raised** — the app's hard-coded 12 fps cap is
  gone. Presets request up to the 30 fps ceiling every agent capture
  path already enforces, and the default (Auto) lets the agent's
  adaptive quality tune the real rate to the measured link, never below
  the legibility floor.
- **Dual-pane Files with a transfer queue** — this computer on the
  left, the device on the right; drag & drop both ways (and from the OS
  onto the device pane); transfers run through a queue with per-file
  progress, speed and cancel. The chunk protocol is unchanged (resumable
  begin/chunk/commit, ranged download, SHA-256 verified); cancellation
  lands at a chunk boundary and a cancelled upload's partial stays on
  the device, resumable by the next attempt. File transfer is now
  select → one click (or drag) — no OS dialogs.
- **Persistent terminal sessions** — the Terminal tab now attaches to
  the agent-hosted persistent shell sessions (present since v1.2 but
  never used by the app): the shell keeps its cwd and environment on
  the device, output is caught up by sequence number on reattach, and
  switching devices or tabs never tears a session down. Command history
  (↑/↓) included; devices without the session service keep the one-shot
  runner as a fallback.

## [1.2.7] — 2026-11-20

- **First-run console discovery** — the agent publishes a small non-secret
  `console-link.json` in its data directory (device code and name, fleet
  address, fleet certificate pin, agent version — no tokens, no keys),
  refreshed as enrollment state changes. The desktop app reads it at
  startup: on a machine whose own agent is enrolled, THIS DEVICE shows the
  real device code and offers one click — "Use this device's fleet" —
  which creates the console's operator key on the spot, takes the fleet
  address and pin from the link, writes the console config and connects.
  When the server does not trust the new console key yet, the app waits in
  a calm "waiting for fleet approval" state with the public key ready to
  copy instead of reporting a connectivity failure.
- **No raw config errors in the app** — a missing client.json is the
  normal unpaired state now; the "Client config not found at ..." file
  exception no longer reaches the status bar.
- **Version truth** — AssemblyVersion, FileVersion and
  InformationalVersion derive from the single `<Version>` in
  Directory.Build.props. They were pinned at 1.2.1 while the release
  version moved on, so a 1.2.5 install reported "v1.2.1" in the app;
  every surface now reports the shipped release, and a bump edits only
  `<Version>` and the installer define.
- **Installer closes the app and tray before upgrading** — the installer's
  PrepareToInstall step terminates EsoAgent.App.exe and EsoAgent.Tray.exe
  before the file copy, so an upgrade no longer fails mid-copy (exit 5)
  on their locked files.
- **Fleet link self-heal** — if the agent connects to the fleet but no
  state frame arrives within 60 seconds (the connected-but-stalled state
  seen on the first start after the v1.2.5 upgrade), it drops the
  connection once and reconnects under the normal backoff instead of
  waiting for a manual service restart.

## [1.2.6] — 2026-11-20

- **Mesh relay fix** — the device-side relay shim ran both directions over a
  single loopback-bound UDP socket, and a loopback-bound socket cannot send
  to a remote relay endpoint: relayed WireGuard traffic silently never left
  the device whenever the fleet relay was on another host. Each shim now
  uses two sockets — loopback for the local WireGuard, wildcard/ephemeral
  for the relay — so relayed peers handshake and pass traffic through the
  fleet relay as designed.
- **Shim port default** — the default first relay-shim port moves from
  51821 to 51831, clear of the fleet server's default UDP relay port
  (51821), so an agent co-hosted with the server no longer hits a bind
  collision ("Address already in use") on its shim.

## [1.2.0] — 2026-11-18

The unification + surpass release: one product, one backend, one device
registry, one activity timeline — with the desktop app as the primary surface,
the web panel as a full second face, and a UI-less operator API/CLI as the
third.

- **Relayed remote desktop** — desktop streaming through the fleet server for
  any enrolled device, with device-side signature re-verification; direct and
  relayed sessions share one protocol (JPEG + raw-diff codecs).
- **One web face** — sign in once at the fleet panel and reach every device's
  full panel through it; per-device "open in browser" links everywhere.
- **Unified activity timeline** — operator, owner, and device events in one
  feed, exportable as JSON/CSV.
- **Tunnels** — TCP port forwards, UDP, and SOCKS5 through the mesh/relay.
- **Session recording** — relayed and direct sessions recorded in the open
  `.esorec` format; list, download, delete from the panel.
- **Owner security** — TOTP two-factor for the owner sign-in, health/offline
  alerts, and signed alert webhooks.
- **Fleet update rollout** — staged, version-gated, in-place updates with
  rollback; same-version and downgrade attempts are refused cleanly.
- **Wake-on-LAN**, device health dashboard, per-device capability toggles.
- **Temporary access codes** — owner-minted, capability-scoped, expiring
  codes that redeem for a signed scoped grant; revocable instantly, fully
  attributed in the timeline. No password sharing.
- **Session extras** — in-session text chat, attended-session consent gate
  (timeout denies), privacy (screen-blanking) mode with honest capability
  reporting, mesh exit node, persistent shell sessions.
- **Operator toolkit** — script library, device scheduled tasks, OS patch
  management, one-call diagnostics, crash watch, deep telemetry, log follow,
  offline command queue, durable jobs with supervision.
- **Perception stack** — UI-structure reading, window management, file-change
  watch, and design-as-data scene dumps for Blender/Unreal/Godot; every
  perception call reports its own cost. Screenshots are the fallback, not the
  interface.
- **Universal workbench** — 21 built-in app-command presets (3D/design, game
  engines, build/IDE toolchains, network tools) that resolve by name on every
  device; a device's own registry entries override same-name presets.
- **App** — file manager, shared activity timeline, Arabic + English UI with
  RTL, tray presence with live state (closing the window hides to tray).
- **Release engineering** — one-command publish, signed update manifest
  (ECDSA P-256, key held only by the release pipeline), Debian packages,
  Inno Setup Windows installer with pre-flight checks and post-install
  self-verification, update modes Auto / Notify / Off.
- **Brand** — the golden wing: parametric SVG masters, full PNG/ICO set, gold
  accent across app and panels.

## [1.1.0] — 2026-11-18

Operator upgrades:

- Durable jobs that survive restarts (interrupted jobs are reported, never
  silently re-run), process supervision with restart policies.
- One-call device inventory and a `doctor` self-check (certificate, disk,
  fleet link, mesh, clock skew, audit health).
- Log follow over the wire; offline command queue with signed, expiring
  envelopes delivered when a device reconnects.
- Environment-variable injection for exec/jobs (values never logged).
- Enrollment now reports the device's real interface addresses.

## [1.0.0] — 2026-11-18

First release:

- Cross-platform agent (Windows service + Linux daemon) with an unrestricted,
  authenticated shell, chunked resumable file transfer, and software/service
  management with post-operation verification.
- Fleet server with device codes (`XXXX-XXXX`), owner-approved enrollment,
  and a signed-command relay — devices verify the operator's signature
  themselves, so the relay cannot forge commands.
- WireGuard mesh coordination with per-device virtual IPs, ACLs, subnet
  routes, and a relay fallback.
- Remote desktop (direct) with view/control modes and a persistent
  active-session banner.
- Owner web panel (PBKDF2 sessions, CSRF, login lockout) — the owner alone
  holds operator revoke, operator-key rotation, and password change.
- Kill switch, signed self-update with rollback, full audit log.
- Desktop app (Avalonia) with device list and Status / Desktop / Terminal
  workspaces; operator CLI (`eso`) for every surface.
