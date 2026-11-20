# Changelog

All notable changes to EsoAgent are documented here. The project follows
semantic versioning; the version lives in one place (`Directory.Build.props`)
and is stamped on every surface — agent, fleet server, client, and app.

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
