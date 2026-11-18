# EsoAgent

<img src="assets/brand/png/logo-lockup-dark.png" alt="EsoAgent — the golden wing" height="72">

**Your machines, under your control — one agent, every OS, a whole fleet.**

EsoAgent is a maximum-control remote administration platform built for one owner.
It replaces the fragile SSH-over-Tailscale workflow with something stronger on
every axis: an unrestricted remote shell at service privilege, a full software /
services / repair layer, an owner control panel served by every device, a fleet
hub that reaches devices behind any NAT, a TeamViewer-class remote desktop, and
a private WireGuard mesh coordinated by the fleet server.

No third-party servers. No telemetry. No phoning home. Everything runs between
machines you own.

---

## What makes it different

- **Zero-trust signed execution.** Every operator action — every command,
  every desktop or tunnel session — is signed by the operator's private
  key, and **each device verifies that signature itself** against the key
  it pinned at enrollment. The fleet server relays but holds no signing
  key, so the server *cannot forge an operator command*, even if it is
  compromised or curious. (Some commercial tools market this as a
  headline feature; here it is simply how the protocol works, since v1.0.)
- **One product, three faces.** The desktop app, the browser panels and
  the `eso` CLI are views over one backend, one device registry and one
  unified activity timeline. Do something in any face and it is visible —
  and auditable — in all of them.
- **Structured data is the native tongue.** Every surface speaks typed
  JSON first; human text is a rendering of it (`eso --json` prints the
  protocol verbatim). The system is operated *in computer language* —
  exact, diffable, verifiable — not in screenshots and guesswork.
- **Screenshots are the fallback, not the interface.** The perception
  stack answers "what's on this screen?" from UI structure trees, app
  data APIs and event streams before pixels are ever touched — and every
  answer carries a cost meter (see USER-GUIDE §10).

---

## The owner's control panel — this is your tool first

Every agent serves **its own web dashboard** on its HTTPS listener (tailnet +
loopback only). Log in with the **owner password** you set at install:

- **Overview** — OS, CPU/RAM/disks, battery/AC, uptime, agent version, connection state.
- **Processes** — live list, kill button.
- **Software** — installed programs; install by id/name, uninstall, upgrade.
- **Services** — list, start/stop/restart, startup mode.
- **Run** — your own terminal into the machine (shell selector: PowerShell / cmd / bash / sh).
- **Desktop** — live remote desktop (view or control), quality + monitor switch, clipboard sync.
- **Files** — browse, download, upload.
- **Log** — the full audit log, filterable.
- **Access** — **your master controls**: pause/resume the operator's API access
  (the kill switch), rotate the API token (the old one dies instantly; the new
  one is shown once), view the certificate fingerprint, change your password.

On Windows, the tray icon's menu opens the same panel
(`https://localhost:8737/panel/`). From your phone on the tailnet, open
`https://<device-tailnet-ip>:8737/panel/` — same panel, same password.

The **fleet server** serves a fleet panel at the same `/panel/` path: every
device (online first), pending enrollment approvals, a per-device console,
group management, subnet-route approvals, ACL editing, enrollment keys,
the fleet-wide audit log — and the operator master controls (below).

---

## What it can do

### The unrestricted shell (the anchor)

The agent's core is an **unrestricted remote shell at the service's privilege
level** — LocalSystem on Windows, root on Linux — exactly SSH's power, not a
subset. `exec` / `powershell` / `shell` accept **any** command: no whitelist, no
allowlist, no blocked-command list.

> **Anything SSH could do, this does — plus** structured software & services
> management with self-verification, chunked resumable file transfer with
> SHA-256 verification, a live owner panel, fleet relay through NAT, remote
> desktop, and a private mesh — all authenticated, all audited.

The structured endpoints are conveniences on top of the open shell, never
gates for it. The only controls are: HMAC/operator-signature authentication
(only the owner's clients), the audit log, and the owner's kill switch.

### Capabilities (identical API on every OS)

- **exec / shell / powershell** — any process with args, cwd, env, stdin, timeout;
  a WebSocket variant streams output live.
- **launch-detached** — processes owned by the service that survive everything
  short of a reboot; the agent tracks its own children.
- **files** — chunked upload/download with per-file SHA-256 verification and
  resume offsets.
- **processes** — list (name/pid/parent/session/user/memory), kill by PID.
- **status** — OS/build, CPU, RAM, disks, battery + AC, uptime, logged-on users,
  tailnet IPs, fleet + mesh state.
- **power** — reboot / shutdown / sleep.
- **logs** — the agent's audit log, or any file, tailed remotely.
- **screenshot + interactive exec** — via the user-session companion (tray on
  Windows, session tools on Linux); a clean error when no session is available.
- **software** — list installed programs (Windows registry + winget merged;
  Linux dpkg/rpm/pacman); install by winget id / package name or from a local
  installer file (MSI → `msiexec /qn /norestart`; EXE → silent args); uninstall
  with silent switches resolved from `QuietUninstallString`; upgrade one or all.
  **Every mutation verifies itself** by re-querying and reports verified vs failed honestly.
- **services** — list/start/stop/restart + startup mode (Windows services, systemd units).
- **repair toolkit** — named maintenance actions through the same audited channel:
  Windows `sfc`, `DISM /RestoreHealth`, Windows Update status, disk-cleanup summary;
  Linux package fix-broken, journal vacuum, disk summary.
- **registry** (Windows) — read/write HKLM/HKCU values; every write is audit-logged with old → new.
- **self-update** — signed packages (SHA-256 + token-HMAC signature), staged,
  applied by a detached script with backup + health check + automatic rollback.
- **remote desktop** — see below.
- **mesh networking** — see below.

### Remote desktop (TeamViewer-class)

- Hosted by the user-session companion (session 0 cannot capture or inject).
- Windows capture: DXGI Desktop Duplication primary, GDI fallback.
  Linux: X11 via scrot/import + ImageMagick. **Wayland returns a clean
  "not available on Wayland yet"** — portal capture is a later phase; nothing is faked.
- Streaming: adaptive JPEG with frame-diffing (unchanged frames are skipped),
  5–15 fps admin-grade on modest links; multi-monitor enumeration + switch.
  The frame channel is **codec-negotiated**, so H.264 (Media Foundation / VAAPI)
  slots in as phase 2 without protocol changes. v1 is deliberately honest:
  adaptive JPEG, not 60 fps hardware video.
- Input: mouse + keyboard via SendInput (Windows) / xdotool (Linux X11).
  Two modes, explicit in the handshake: **View-only** and **Control**.
- Clipboard text sync, both directions.
- While a session is live, the companion shows a persistent on-screen banner —
  **"جلسة تحكم نشطة — Active control session"** — and session start/stop/mode
  are audit-logged. The owner's kill switch / pause ends any live session immediately.
- Ctrl+Alt+Del: on Windows the Secure Attention Sequence can only be sent by a
  SYSTEM-level service when the "Software SAS generation" policy allows it; the
  tray (a user process) attempts it and it may no-op — that is an OS limit, not
  a missing feature. On Linux/X11 the combo is injected as ordinary keys.

### Fleet mode

Devices enroll to **EsoAgent.Server** (the fleet hub — run it on any always-on
machine you own; a cloud box, a LAN PC). Each device has:

- an ECDSA P-256 **device keypair** and a human **device code** (`XXXX-XXXX`,
  derived from the public key, stable for the life of the install),
- a device name (hostname by default, owner-editable),
- a persistent **outbound** WebSocket to the server (heartbeat + command
  channel) — **no inbound ports on devices**, works through NAT/firewalls.

Enrollment: the device presents its code + public key → it appears as
**Pending** → the owner (panel) or the operator (CLI) approves it → only then
can it receive commands. Unapproved devices get nothing. For unattended
installs, the owner/operator can mint **enrollment keys** (one-time or
multi-use; only the hash is stored) that pre-approve a device presenting them.

Addressing: **by code** (relayed through the server), **by IP** (direct,
standalone path), **by name** (mesh, below), plus `--group <name>` and `--all`
fan-out with per-device aggregated results — failures are always reported per
device, never silently dropped.

**Trust model (honest):** commands are signed by the operator key. The server
relays and authenticates both sides, but **devices verify the operator
signature themselves — a compromised relay cannot forge commands.** Per-device
HMAC tokens remain for direct mode. Enrollment requires approval — there is
no self-joining.

### The mesh (Tailscale-class networking)

Fleet membership includes a private **WireGuard** mesh (EsoAgent drives the
platform's native WireGuard tooling — it does not reimplement a VPN):

- The server assigns each approved device a stable virtual IP from
  **10.77.0.0/16** and distributes peer configs; agents regenerate + reload
  their WireGuard config on changes.
- **Name resolution (MagicDNS-lite):** devices are reachable by name — the
  server resolves name → virtual IP for client and panel, and agents keep a
  marked block in the OS hosts file in sync. (A local DNS responder is phase 2.)
- **Connectivity:** direct peer-to-peer WireGuard wherever NAT allows;
  **relay fallback** — when a peer has no reachable endpoint, traffic goes
  through the server's DERP-style UDP relay, keyed by WireGuard public keys.
  The relay sees only ciphertext.
- **Subnet routing:** a device may advertise LAN subnets; they take effect
  only after owner/operator approval, and are then distributed as routes.
- **ACLs:** allow rules (device/group → device/group, optional ports) compiled
  into per-device firewall plans. Default posture: fleet members can reach
  fleet members' agent ports; everything else is deny-by-default at the
  device firewall.
- **Phase 2 (named, not built):** exit nodes, local DNS responder (full
  MagicDNS), public exposure (Funnel-like), H.264 desktop.

---

## v1.2 — surpass + unify

- **Relayed desktop** — any enrolled device's desktop streams through the
  fleet server when direct access is impossible (the device verifies the
  session signature itself; the relay gains no power). Privacy mode
  (blank screen + input lock), in-session chat, attended-session consent
  prompts, and per-session recording to a documented file format.
- **One web face** — sign in once at the fleet panel and open any device's
  full panel (status / desktop / files / activity) inside it.
- **Unified activity timeline** — device audit, server events, operator
  actions, owner logins, alerts: one feed in the app, the panel and
  `eso activity`, exportable as JSON/CSV.
- **Owner 2FA (TOTP) + alerts + signed webhooks** — offline devices, low
  disks, exhausted supervision, failed logins, application crashes.
- **Tunnels** — TCP, UDP and dynamic SOCKS5 forwarding over direct and
  fleet channels; Wake-on-LAN; fleet-wide signed update rollout with
  per-device results; OS patch management; a script library; device-side
  schedules; per-capability toggles; a health dashboard; Arabic + English
  UI everywhere.
- **Operator supremacy pack** — one-call diagnostics (`eso diagnose`),
  deep telemetry with threshold events, crash watch, all-monitor/window/
  region vision, UI-structure reading with click-by-element, window
  management, file-change watch, persistent shell sessions, a GUI launch
  harness, and a mesh exit node.
- **The universal workbench** — a config-only app-command registry
  (exec / verify / perceive) with preset families for Blender, Unreal,
  Unity, Godot, GIMP/Photoshop, the dotnet/git/node toolchain and a
  polyglot set (Python, Go, Rust, Maven, CMake — `eso app detect` finds
  the right entries for a project folder), plus a network-work pack.
  Perceive returns designs as *data*: scene/level JSON, exact ID-pass
  measurements, diffable dumps.
- **The efficiency layer** — delta perception (diffs, never repeat
  dumps), anomaly-only event subscriptions, intent-level operations with
  proof objects, tiered perception, and quality-floored adaptive
  streaming (floor: quality 40 / 4 fps — reported honestly when even the
  floor can't be carried).

See USER-GUIDE.md for the human walkthrough, BENCHMARKS.md for measured
numbers and method, CODE_SIGNING.md for the signing policy,
FEATURE-MATRIX.md for the audited coverage of the
whole remote-access field, ROADMAP.md for the honest remainder.

---

## Access tiers — who can do what

Two tiers, exactly:

- **Operator** (the signed key / the `eso` CLI): **full fleet control** —
  every capability on every device, by code or IP, groups and fan-out,
  software/services/files/power, desktop sessions, mesh/ACL/subnet management,
  config distribution, **and** operational management: approving/rejecting
  enrollments, renaming devices, managing groups, rotating per-device tokens,
  minting enrollment keys.
- **Owner** (the dashboard password): everything the operator has, **plus
  three master controls reserved to the owner alone**, never exposed to the
  operator key: **(1)** pause/revoke the operator's access, **(2)** rotate the
  operator key, **(3)** change the owner password.

> **The operator can do everything operationally; the owner alone holds the switch over the operator.**

The operator cannot lock the owner out or revoke itself. The owner can cut
the operator off at any time from the panel — the server refuses its calls
and every approved device drops its key.

Per-device, the same hierarchy exists in miniature: the device panel's Access
tab (owner) can pause the API token's access and rotate the token; the API
token itself can do neither.

### How the owner revokes access (any of these, any time)

1. **Panel → Access → Pause** (per device) or **Revoke operator** (fleet-wide).
2. **Tray kill switch** (Windows) / `EsoAgent.Linux disable` (Linux) — refuses
   all commands until re-enabled locally. Locking is a local act; it can never
   be lifted remotely.
3. **Rotate the API token / operator key** from the panel — old credentials
   die instantly.
4. **Stop the service** (`Stop-Service EsoAgent` / `systemctl stop esoagent`).
5. **Delete the token file** on the device, or **uninstall**
   (`uninstall.ps1` / `uninstall.sh`).

---

## Security model

- **Transport:** TLS everywhere, self-signed certs **pinned by SHA-256
  fingerprint** on every client (agent, panel, fleet). A self-signed cert with
  pinning is as strong as a CA cert for this single-owner design.
- **Binding:** agents listen **only** on the machine's Tailscale interface
  (100.64.0.0/10) + loopback — never on all interfaces. Installers add a
  firewall rule scoped to 100.64.0.0/10 as a second layer.
- **Direct API auth:** per-install 256-bit token; every request is
  HMAC-SHA256-signed (method + path + timestamp + body hash) with a ±120 s
  anti-replay window and constant-time comparison.
- **Fleet auth:** ECDSA P-256 signatures — operator key for commands/requests,
  device keys proven by challenge-response at connect, panel key for
  owner-panel actions. Devices pin the operator + panel keys they were given
  at approval time.
- **Owner auth:** password stored as a salted PBKDF2-SHA256 hash
  (210,000 iterations, BCL `Rfc2898DeriveBytes`); session cookie HttpOnly +
  SameSite=Strict; CSRF token on every mutation; login rate-limit with
  lockout backoff (5 failures → 30 s, doubling to 15 min).
- **Audit:** every command is appended to a rolling local log (timestamp,
  endpoint/action, summary, result) — on the device, and on the server for
  fleet actions. Registry writes log old → new. Desktop sessions log
  start/stop/mode. The owner can read the log any time (panel Log tab, tray
  "عرض السجل", `eso tail`).
- **Updates:** packages must carry a SHA-256 + a token-HMAC signature over it;
  application is atomic-ish (backup → swap → health check → rollback).
- **No third-party anything:** no telemetry, no external assets in the panels
  (all HTML/CSS/JS is embedded), no update servers, no accounts.

---

## Architecture

```
EsoAgent.Core      (net8.0, platform-neutral)
  protocol + DTOs · HMAC auth · audit log · kill switch · owner auth (PBKDF2/sessions/CSRF)
  owner dashboard (embedded SPA) · capability endpoints · file transfer (chunked, resumable)
  update service · device identity + operator signing · fleet client (outbound WSS)
  desktop protocol + frame pipeline (rawdiff codec) · mesh: X25519, IP allocation,
  WireGuard config builder, ACL compiler, hosts sync, relay framing + shims, mesh applier
  platform abstractions: IProcessRunner, ISystemInfo, IPowerControl, IScreenshotProvider,
  IInteractiveExec, IPackageManager, IServiceControl, ISystemRepair, IRegistryAccess, IDesktopHost

EsoAgent.Windows   (net8.0-windows)  Windows Service (LocalSystem) + registry/winget/sc.exe layer
EsoAgent.Tray      (net8.0-windows)  WinForms companion: screenshot, interactive exec, desktop
                                     host (DXGI/GDI + SendInput), kill switch, panel link
EsoAgent.Linux     (net8.0)          systemd daemon + dpkg/rpm/pacman + systemctl layer,
                                     X11 desktop host, lock-file kill switch
EsoAgent.Server    (net8.0)          fleet hub: registry, enrollment, signed-command relay,
                                     fleet panel, mesh coordination, DERP-style UDP relay
EsoAgent.Client    (net8.0)          the `eso` CLI — direct / by-code / group / --all
```

**macOS is designed-not-built:** Core is platform-neutral and a launchd head
only needs the platform abstractions implemented; no macOS head ships in v1
because it cannot be compiled or tested here.

### The `eso` CLI

```
eso status                              eso devices
eso exec [--stream] <file> [args]       eso approve|reject <CODE>
eso shell <bash|powershell> <script>    eso rename <CODE> <name>
eso ps <powershell script>              eso groups <CODE> <a,b>
eso launch <file> [args]                eso rotate-device-token <CODE>
eso put <local> <remote>                eso mesh / resolve <name>
eso get <remote> <local>                eso enroll-key create|revoke
eso shot out.png                        eso subnets <CODE> <cidrs>
eso software list|install|uninstall     eso acl set rules.json
eso services list|start|stop            eso desktop --device <CODE|IP>
eso keygen                              (mint this operator's keypair)
Targeting: --device <CODE|IP> | --group <name> | --all
```

Client config (`~/.esoagent/client.json`): host, port, cert fingerprint, token
(never printed), plus fleet URL/fingerprint and the operator key path.

---

## Install

**Windows** (from the published package, as Administrator): `install.ps1`
installs to `C:\Program Files\EsoAgent`, generates the token, prompts for the
owner password, registers the service + tailnet-scoped firewall rule + tray
autostart, and prints the summary (tailnet IP, port, cert fingerprint, token —
shown once). Uninstall: `uninstall.ps1`.

**Linux** (as root): `install.sh [--fleet-url …] [--mesh]` installs to
`/usr/local/lib/esoagent` with data under `/etc/esoagent`, writes the systemd
unit (`KillMode=process`, so detached children survive restarts), generates
token + owner password (`ESOAGENT_OWNER_PASSWORD` for unattended installs),
and prints the same summary. Uninstall: `uninstall.sh`.

**Fleet server**: run `eso-server` anywhere always-on; set its owner password
(`eso-server set-owner-password`), set the operator key
(`eso-server set-operator-key <base64-spki>` from `eso keygen`), and point
agents at it in `esoagent.json` (`fleet.serverUrl` + `serverFingerprint`).

**Build from source:** `dotnet build EsoAgent.sln` (0 errors), tests via
`dotnet test`, packages via `scripts/publish.sh` / `scripts/publish.ps1`.

---

## Verification status (honest)

- Unit tests: 52 passing (crypto, signing, parsers, file transfer, owner auth,
  desktop pipeline, mesh config/ACL/allocation, X25519 cross-checked).
- Linux end-to-end smoke tests (daemon + panel + fleet + tiers + mesh
  coordination) run in the build sandbox — see `BUILD-REPORT.md`.
- **Pending first real install:** live remote desktop (DXGI/SendInput on a real
  Windows session), live WireGuard mesh + UDP relay (the build sandbox blocks
  UDP entirely), and the Windows service's live install test on the laptop.
  Nothing here is claimed working that was not actually run.
