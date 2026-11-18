# EsoAgent — Roadmap

Honest status for everything deliberately *not* in the current release.
One line each: what it is, where it stands, what it waits on. Nothing here
is silently dropped; nothing here is promised with a date.

## From the remote-access census

- **Remote printing** — redirect a device's print jobs to a local printer.
  Not built; needs a virtual-printer driver per OS.
- **USB / smartcard redirection** — pass local USB devices into a session.
  Not built; kernel/driver work, Windows-first if attempted.
- **Remote audio streaming** — hear the device's audio in a session.
  Not built; the desktop protocol has no audio channel yet.
- **Virtual displays** — a headless device gains a virtual monitor.
  Not built; platform display drivers differ per OS.
- **View-only session share links** (Guacamole-class) — a browser link a
  third party can watch (not control) with. Not built; the relayed-stream
  plumbing it needs now exists.
- **SSO / SAML / OIDC + SCIM owner sign-in** — enterprise identity for the
  panels. Not built; owner auth is local password + TOTP today.
- **Whitelabel / custom-client branding generator** — rebrand the app per
  deployment. Not built; strings/assets are externalized, which is the
  prerequisite.
- **Intel AMT / vPro out-of-band module** — control below the OS
  (MeshCentral is the reference). Not built; needs AMT-capable hardware
  to develop against.
- **Layer-2 bridging** (ZeroTier-class) — the mesh is Layer-3 (WireGuard)
  today. Not built; L2 needs TAP drivers per OS.
- **Public service publishing with edge auth** (Funnel/ngrok-class) —
  expose one device service on a public URL. Not built; tunnels exist,
  the public edge does not.
- **AR camera assist** — phone-camera guided help. Not built; needs the
  mobile apps below first.
- **Mobile apps (Android/iOS)** — the app is desktop-only today. Not
  built; the API surface they would use is stable and shared.
- **Multi-operator roles** — today there is one operator role (full) and
  one owner. Scoped roles (read-only operator, per-device operators) are
  designed-for but not built.
- **Windows safe-mode reboot** — reboot a device into Safe Mode with the
  agent still reachable. Not built; needs boot-config work + a real
  Windows test cycle.
- **H.264 hardware encoding** — the desktop stream is JPEG + region-diff
  today. Not built; per-platform encoder backends (NVENC/QSV/AMF/
  VideoToolbox) are the work.

## v1.2 honest remainders

- **Unreal object-ID pass** — semantic renders exist for Blender; Unreal's
  ID pass needs a shipped post-process material asset. Not built.
- **Wireframe-overlay pass** — specified with the semantic renders;
  headless verification is outstanding. Not built.
- **Linux desktop input lock** — privacy mode blanks the screen on Linux
  (DPMS) but cannot yet lock local input there; Windows locks fully.
- **Linux inbound session chat** — chat is device→operator via
  notification on Windows; on Linux it is operator→device only
  (notify-send has no reply surface).
- **Mesh exit-node NAT on Windows** — exit-node routing is configured on
  all platforms; automatic NAT is Linux-only today (Windows is a manual
  step, stated in the device status).
- **Exit node / peer mesh traffic** — verified pairwise only when a
  second enrolled device exists; single-device deployments exercise the
  coordination, not the traffic.
- **Windows Update per-id patch selection** — patch apply on Windows
  applies all pending updates (COM API path); per-id selection is
  Linux/apt-only today.
- **SOCKS5 / UDP tunnel unit coverage** — implemented and exercised via
  the live test script; the sandbox cannot run .NET loopback, so these
  have live coverage rather than unit coverage.
- **UI-automation event subscriptions** (value-changed etc.) — window
  open/close/focus events ship today (tier 3); deep UIA/AT-SPI event
  listeners need a session-resident listener process. Not built.
- **Perceive scripts live verification** — Blender/Unreal/Godot perceive
  scripts are code-complete and sentinel-tested, but no machine in the
  build environment runs those apps; first live run is a checklist item.

## Original R&D directions (first-of-their-kind, invented for EsoAgent)

- **Semantic scene-graph streaming** — stream a design's *diff of meaning*
  (object moved, material changed) as a standing subscription, so an
  operator "watches" a 3D scene for the cost of its changes only.
  Direction only; delta perception is its foundation.
- **Predictive operator-intent prefetch** — the agent notices the shape
  of a diagnostic session and pre-positions the likely next evidence
  (logs, dumps) before it's asked for. Direction only; must never cost
  more than it saves — the cost meter decides.
- **Self-describing capability negotiation** — devices and bridges
  advertise typed capabilities (verbs, schemas, evidence classes) so any
  client composes operations without version coupling. Direction only;
  the app-command registry is the seed.
- **Glance-equivalence testing as a CI gate** — the human-glance
  checklist (item: perception stack) run automatically against standard
  desktop scenarios, failing the build when a question regresses to
  needing pixels. Direction only; the checklist + cost meter exist.

## Platform notes

- **macOS agent head** — the core is cross-platform; a macOS service +
  desktop backend is not built yet.
- **More human languages** — AR/EN ship; adding a language is a resource
  file by design (see USER-GUIDE §12). Community translations welcome.
