# EsoAgent — User Guide

**One product for all your machines.** EsoAgent lets you see, control, fix and
automate every computer you own — Windows or Linux — from one place. There are
three faces of the *same* system, all showing the same devices and the same
activity history:

- **The desktop app** (the main face) — a window on your PC with your device
  list, remote desktop, terminal, file manager and activity feed.
- **The browser panel** (the bonus face) — the fleet panel in any browser;
  sign in once and open any device from it.
- **The command line (`eso`)** — for scripts and automation.

Nothing here needs an AI, an account with any company, or anyone else's
servers. Your fleet server runs on a machine you own; your devices talk to it;
that's the whole architecture.

---

## 1. Install

### The fleet server (install once, on an always-on machine)

```
# Linux
unzip esoagent-server.zip && cd linux
./eso-server set-owner-password        # choose the owner password
./eso-server set-operator-key <key>    # only if an operator CLI will be used
./eso-server                           # serves on port 8740 (HTTPS)
```

Run it as a service with the included systemd unit / Windows service wrapper
so it survives reboots.

### A device (every computer you want to control)

**Windows:** unzip `esoagent-windows.zip`, then in an elevated PowerShell:

```
.\install.ps1
```

**Linux:**

```
unzip esoagent-linux.zip
sudo ./install.sh
```

Both installers ask for (or generate) the device's owner password and print
the address of the device's own panel. The agent runs as a system service
from then on — no login needed on the device itself.

### Point the device at your fleet

In the device's config (`esoagent.json`, in the agent's data folder) set:

```json
{ "fleet": { "url": "https://<your-server>:8740", "name": "laptop" } }
```

Restart the agent service. The device appears in the fleet panel as
**Pending** with a device code like `XXXX-XXXX`.

## 2. Enroll (approve) a device

1. Open the fleet panel or the app.
2. Find the pending device, check the code matches what the device shows,
   click **Approve**.
3. Done — the device is part of your fleet. Its desktop, files, terminal and
   health now appear everywhere.

You can also mint **enrollment keys** in the fleet panel (Access tab) so a
device you install yourself is approved automatically.

## 3. The app — your daily driver

- **Devices** on the left: online first, with health badges (⚠ / ✖) when a
  device needs attention, and a badge with the number of alerts in the last
  24 hours.
- **Status** tab: what the machine is, what it's doing, and a **Diagnose**
  button that answers "what's wrong?" in one click (see §9).
- **Desktop** tab: live remote desktop. View or control; if the device can't
  be reached directly, the session automatically flows through your fleet
  server instead — same screen, same controls.
- **Terminal** tab: a shell on the device.
- **Files** tab: browse, upload, download, rename, delete — no command line
  needed.
- **Activity** tab: everything that happened, on every device, in one feed —
  who did what, when, and whether it worked. Export it as JSON/CSV.
- **Access** tab (fleet devices): hand out temporary control codes — see
  §6, "Sharing a device with an access code".
- **Open in browser**: jumps to this device inside the fleet panel.
- **Language**: the header toggles **English / العربية** (the panels do the
  same; Arabic renders right-to-left).

**Closing the app doesn't quit it.** The window's X button tucks the app
into the tray — it keeps running, and the first time this happens a hint
says so. The only full quit is the tray icon's **Quit** menu entry. The
agent service on each device is separate and always runs either way.

**The tray icon** (on devices and on your own machine) *is* the status:
the golden wing is **gold** when online, **grey** when offline, and its
signal waves **light up and pulse** while someone is controlling the
device. Its menu shows the device name and code (click to copy), the
mesh IP (click to copy), "Create access code" (opens the fleet panel,
where codes are minted after owner sign-in), Pause/Resume, Open app, and
Open panel. Resuming a paused agent from the tray asks for the owner
password — nothing owner-only ever runs from the tray without it.
On a headless Linux server there is no tray (honest limitation): use the
CLI or the panel there instead.

## 4. Your first remote session

1. Select a device → **Desktop** tab → **Connect**.
2. You're seeing the device's screen. In **Control** mode your mouse and
   keyboard drive it; in **View** mode you only watch.
3. Useful session tools:
   - **Privacy**: blanks the device's screen and locks its keyboard/mouse
     while you work (the person at the device sees a notice). Both ends show
     that privacy is on; it's written to the activity log.
   - **Chat**: a text box under the screen — message the person at the
     device, and read their replies, without opening anything else.
   - **Record**: tick *Record* before connecting and the session is saved to
     a recording file on the device (download it later from the device's
     Files → recordings, or the device panel).
   - **Clipboard** syncs both ways unless the device's owner turned it off.
4. **Disconnect** when done. Everything you did is in Activity.

If the device's owner set a **consent policy**, a control session first asks
the person at the device to accept (the prompt times out to *No* — nobody can
be surprised by a session).

## 5. Files without the command line

The app's **Files** tab and the panels' Files tab browse the whole device.
Uploads and downloads are chunked and hash-verified — a interrupted transfer
resumes instead of restarting. Recordings of desktop sessions live in the
device's data folder and are listed next to the files.

## 6. Staying in control (owner settings)

The **owner** — whoever holds the owner password — has three master controls
nobody else has: **revoke the operator** (stops all operator access at once),
**rotate the operator key**, and **change the owner password**. They're in
the Access tab of either panel.

Also in Access:

- **Two-factor login (2FA)**: enroll an authenticator app (TOTP) for panel
  logins. Strongly recommended. If you ever lose the authenticator, run
  `disable-2fa` on the device/server itself to recover.
- **Kill switch**: pauses the operator API on a device instantly.
- **Capability toggles** (per device): independently switch off file
  transfer, clipboard sync, tunnels, or desktop control. The agent enforces
  them; every surface shows what's off.
- **Consent policy** (per device): require the local user's accept for
  control sessions (and view sessions, if you want).
- **Alert webhook**: alerts can also be POSTed, signed, to a URL you choose
  (a chat bot, a ticket system…).

**Alerts** appear in the activity feed and as badges: a device offline too
long, a disk nearly full, a supervised program that gave up restarting,
repeated failed logins, an application crash.

### Sharing a device with an access code

Sometimes you want to hand control of one device to someone — a helper, a
second machine, an AI operator — **without** giving them your password or
an account. That's an **access code**:

1. In the fleet panel (Access tab), the app (Access tab), or with
   `eso access grant`, create a code for the device. You choose what it
   may do (for example: view the desktop only, no file transfer), when it
   expires (minutes to days), and optionally how many times it can be
   used.
2. Give the short code (like `K7QX-4M2P`) to whoever should get in.
3. They redeem it (`eso access redeem K7QX-4M2P`, or their client does)
   and receive a signed grant for **that device only**, with exactly the
   powers you chose — never the owner powers (they can't revoke the
   operator, rotate keys, change passwords, or create more codes).
4. You see every live grant, and everything done under it, in the
   activity feed — attributed to the grant. **Revoke** any code or grant
   and it stops working on the very next call.

The code itself is never stored — only a hash of it — and it's shown to
you exactly once, when you create it.

## 7. Handy commands (`eso`)

```
eso status                     # what is this machine, is it healthy
eso exec <file> [args]         # run something, get structured output
eso shell bash -c "..."        # one-off shell
eso sessions                   # persistent shells that survive disconnects
eso tunnel 8080 <device> 127.0.0.1:80          # forward a local port (TCP)
eso tunnel 5353 <device> 8.8.8.8:53 --udp      # …or a UDP service
eso tunnel --socks 1080 <device>               # dynamic SOCKS5 through the device
eso wake <CODE|name>           # Wake-on-LAN a sleeping machine
eso patch check|apply          # pending OS updates, then apply + re-check
eso rollout --all              # push the signed update package to the fleet
eso scripts run <name> --all   # run a saved script everywhere
eso activity                   # the unified feed, in your terminal
eso bench                      # measure this deployment (writes BENCHMARKS.md)
```

Add `--json` before any device command to get the raw structured result —
EsoAgent speaks structured data natively; the human text is a rendering.

Devices can also run their **own schedules** ("the device's cron"): named
commands at an interval or a daily time, kept in the device's config, with
results in the activity feed. And **scripts** saved on the fleet server form
a library your whole fleet can run — every run is logged with the script's
exact contents hash and who ran it.

## 8. Tunnels — reach anything a device can reach

A tunnel makes a service on (or reachable from) a device appear on a local
port of yours — the SSH `-L` idea, over the direct connection or through the
fleet server, for devices behind any NAT. SOCKS mode turns the device into a
dynamic exit for any SOCKS-aware program. Every tunnel is logged.

One device can also be the fleet's **exit node** (fleet panel → Mesh): other
devices that opt in route all their traffic through it.

## 9. "What's wrong with this machine?"

Press **Diagnose** (app Status tab) or run `eso diagnose`. One call collects:
OS error events, crashed programs (with dump paths), failed services,
resource pressure (RAM/disk/GPU), the built-in doctor checks, and the agent's
own recent errors — one structured report, worst problem first.

For ongoing watching, `eso telemetry` shows per-process CPU/RAM, GPU,
disk IO and network connections; threshold crossings (set in the device
config) appear in Activity on their own — nobody has to poll.

## 10. How the operator *sees* — the perception stack

EsoAgent is built so an operator (human or AI) perceives a machine the way
the machine itself understands it — **screenshots are the fallback, not the
interface**. The stack, strongest evidence first:

1. **UI structure trees** — every window as a tree of elements (name, role,
   value, bounds). Text is *read* from controls, exactly; actions target an
   element, never a guessed pixel. `eso see` returns this first, with a
   **glance summary**: what's open, what's focused, what text is shown, what
   changed since the last look, and where the error/dialog is.
2. **App data APIs** — the workbench's *perceive* verb (§11): the program's
   own model of its content (a 3D scene as JSON, a level as JSON).
3. **Event streams** — window open/close/focus, file changes, crashes and
   threshold crossings are *pushed* as compact events (`eso events`,
   Activity). Silence means nothing changed; nobody burns effort polling.
4. **Semantic renders** — when spatial measurement is the question: object-ID
   passes where every object is one flat colour, so coverage and bounding
   boxes are computed *exactly* from the render plus its ID map.
5. **Delta pixels** — captures that ship only what changed.
6. **Full screenshot / OCR** — the last resort, for a human eye or when
   nothing above exists. OCR output is the weakest evidence class wherever
   it appears.

Every `see` / perceive answer carries a **cost meter** (its size in bytes ≈
tokens), so consumption is visible, never guessed. And perception is
*tiered* — cheap first, deep only on signal:

```
status → doctor → diagnose → telemetry / see → perceive / full tree → pixels
```

Two more consumption savers, both quality-floored:

- **Delta perception**: repeat views of inventory / telemetry / UI trees /
  scenes return *structural diffs only* — a full dump is sent once, never
  twice (`?delta=1`, `eso app perceive <name> --delta`).
- **Intent operations**: one declarative call — `eso intent ensure-service
  <name> running`, `ensure-software <id>`, `ensure-file <path> --sha256 …`,
  `ensure-supervised <name>` — executed *and verified* on the device,
  returning a compact proof instead of a dozen round-trips.
- **Adaptive streaming**: the desktop stream adapts quality/FPS to measured
  bandwidth but never below the legibility floor (**quality 40, 4 fps**);
  if even the floor can't be carried, it says so instead of silently
  degrading.

## 11. The universal workbench — working *inside* other programs

If a tool can be scripted, EsoAgent works inside it. The **app-command
registry** (in the device's `esoagent.json`, under `"appCommands"`) maps a
name to an executable plus an argument template with `{placeholders}`:

```json
{ "name": "blender", "executable": "C:\\Program Files\\Blender\\blender.exe",
  "argsTemplate": "--background \"{blend}\" --python \"{script}\"" }
```

Three verbs per entry, all from `eso app`:

- `eso app run <name> key=value …` — run it, with full output capture (and,
  for entries with a log-scan glob, the newest log's errors surfaced).
- `eso app verify <name> key=value …` — the entry's *verify template* (a
  build, a test, a probe): PASS/FAIL plus evidence.
- `eso app perceive <name> key=value …` — the program's content as
  structured data (see below).

**No code change, no rebuild — the registry grows by config, forever.**
Presets ship for whole families and are **runnable by name immediately** —
no registration step. Registering an `appCommands` entry with the same
name **overrides** the preset, which is how you point one at your
install's real executable path:

- **3D / design**: Blender (full bridge + perceive), GIMP (Script-Fu batch),
  Photoshop — *degraded, labelled as such*: it has no stable headless CLI,
  so the honest bridge is launch + window/input control.
- **Game engines**: Unreal (editor Python + commandlets + perceive), Unity
  (batchmode methods; perceive = your own editor method that prints the
  sentinel JSON — the guide in `PerceiveScripts` shows the shape), Godot
  (headless scripts + scene perceive).
- **Build / IDE toolchain**: dotnet build/test, git, node, plus the polyglot
  set (Python, Go, Rust/Cargo, Maven, CMake). `eso app detect <folder>`
  looks at a project folder and tells you which entries to register for it —
  any language whose toolchain exists on the device works this way.
- **Network pack**: interfaces, routes, connections, ping, and a port probe
  whose *verify* form feeds straight into `diagnose`.

### Perceive: designs as data, not pictures

For design work, perceive is the primary way of "seeing":

- **Blender**: the whole scene as JSON — objects + hierarchy, transforms,
  dimensions, materials, modifiers, mesh stats, cameras, lights — plus a
  machine-readable **object-ID render** (BMP + legend) from which screen
  bounding boxes, coverage % and occlusion are computed *exactly*.
- **Unreal**: level actors as JSON — class, transform, world bounds,
  components, key properties — plus asset-metadata queries, and
  camera-projected screen bounding boxes computed from the data.
- **Godot**: the scene tree as JSON — node types, transforms, meshes,
  cameras, lights.
- Any app can define its own perceive command the same way (print JSON
  between the `ESO_PERCEIVE_JSON_BEGIN/END` sentinels).

Two dumps **diff** (`--diff`, or `--delta` for agent-side diffing): what
moved, what changed, what appeared — as a list of exact field changes.
Measurements come back as numbers. Screenshots remain available for human
eyes — they are simply not how the operator measures.

## 12. Languages

The interface ships in **English and Arabic** (toggle in the app header and
both panels; Arabic is fully right-to-left). Localization is resource-based:
adding any other human language is a resource file — **zero code change**.
The same is true on the machine side: every surface speaks structured data
first, so translating or re-rendering a result never touches the protocol.

## 13. When something's off

- Run `eso doctor` on the device (or open its panel → Overview): certificate
  days left, clock skew, disk, fleet link, kill-switch state.
- `eso diagnose` for the machine's own problems (§9).
- The **Activity** feed is the ground truth of what happened; export it when
  you need a record.
- A device that won't enroll is almost always a clock or a fingerprint
  mismatch — doctor names both.

## 14. Installing & updates

**Installing.** Get the installer for your platform from the project's
GitHub **Releases** page:

- **Windows**: `EsoAgent-Setup-<version>.exe` — a proper wizard. It
  detects an existing install (and upgrades it in place), checks disk
  space before touching anything, registers the service + tray, then
  *verifies itself*: service running, self-check answering, panel
  reachable. The finish screen shows your **device code** and how
  approval works, and offers to open the app. Silent deployment for IT:
  `EsoAgent-Setup.exe /VERYSILENT /FLEETURL=https://your-fleet:8740
  /ENROLLKEY=XXXX-XXXX`.
- **Linux**: the `.deb` package (`sudo apt install ./esoagent_<version>_amd64.deb`),
  or the zip + `install.sh`. Same post-install verification and
  first-run summary.
- **Uninstalling** removes the service and files. Your device **data is
  kept** unless you explicitly choose to remove it (Windows uninstaller
  asks; on Linux only `dpkg --purge` deletes it) — reinstalling later
  resumes as the *same* device.

**Updates.** EsoAgent updates **in place** — never a second install:

- One install directory, one service, one tray. An update swaps the
  binaries inside the existing installation; your device code, keys,
  owner password, config and data are untouched. An updated device is
  the *same* device — never a re-enrollment.
- Updates are **version-gated**: the same version again is a clean
  "nothing to do", and a downgrade is refused. Running an installer or
  updater twice converges to the same single installation.
- If an update fails its health check, the previous binaries are
  restored in the same place (rollback) — no side-by-side copies.
- The update channel is a small **signed manifest** from GitHub
  Releases. Devices verify the signature (a key pinned in the product)
  and every file's hash before applying anything. Checks are light: one
  small fetch at start, then every few hours.

Per-device setting (agent config, "releaseUpdate"):

- **Auto** — download, verify, apply, restart; rollback on failure.
- **Notify** (default) — a badge in the app, tray and panel; you apply
  when you choose (fleet owners can also push a release to a group with
  `eso rollout`).
- **Off** — no checks at all.

Release integrity does not depend on trusting the download server: the
manifest signature is verified against a key the server never holds,
exactly like operator commands. See CODE_SIGNING.md for the full
policy.
