# EsoAgent — Benchmarks

Measured numbers only. Every figure in this file comes from a run of
`eso bench` (or the stated measurement) against a real deployment; the
method is stated with the numbers, the environment is named, and local
numbers are labelled local. No figure is estimated, extrapolated, or
copied from another product's marketing. Where a measurement has not
been run yet, the row says **not yet measured** and gives the method —
an empty cell is honest, a guessed one would not be.

This file is the published benchmark suite defined in
COMPETITIVE-STUDY.md §6: the head-to-head the rest of the field has
never published (their evidence base is a 2014 vendor self-test and
unmeasured marketing claims). Rows are added as runs complete; a row
is never filled from anything but a run.

## How to reproduce

```
eso bench                 # against the configured device (direct or fleet relay)
eso bench --device <CODE> # via the fleet relay (needs the paired operator key)
eso bench --out run.md
```

`eso bench` measures, against the target device:

- **Status round-trip** — 20 × `GET /api/status`, min / median / p95.
- **Exec round-trip** — 10 × a trivial shell exec, min / median / p95.
- **File transfer** — an 8 MiB upload (chunked, SHA-256 verified) and
  the same file back down; throughput in MiB/s.
- **Desktop frame rate** — frames received in ~5 s at quality 60, view
  mode.
- **Cost of knowing the screen** — the byte size of a tree-based
  `eso see` answer vs a full pixel capture of the same desktop.
- **Delta efficiency** — a repeat inventory with delta perception:
  full payload bytes vs the structural diff actually sent.
- **Queue enqueue latency** — how fast an offline command is accepted
  for queued delivery. True *delivery* time needs an offline→online
  cycle and is measured in the live test script, not here.

The 2026-11-22 run script and raw outputs are in
`benchmarks/2026-11-22/` (`run-bench.sh`, `bench-direct.txt`,
`bench-relay.txt`, `environment.txt`).

## Suite status (COMPETITIVE-STUDY.md §6, per measurement)

| # | Measurement | Status |
|---|---|---|
| 1 | Download size | **Measured** 2026-11-22 (table below) |
| 1 | Install time / time-to-first-session / clicks | Not yet measured — needs a fresh VM snapshot and one operator running the stopwatch protocol in §6 |
| 2 | Connect time (click → first frame/prompt) | Not yet measured — `eso bench` exec/status round-trips below are the shell-side proxy until the ≥30-run connect protocol runs |
| 3 | Input-to-photon latency (camera) | Not yet measured — needs the high-speed-camera rig in §6; no number will be published without it |
| 4 | Sustained fps + quality (SSIM) + bandwidth, locked together | Partially: fps alone is measured by `eso bench`; the locked fps+SSIM+bandwidth pass is not yet measured |
| 5 | CPU/RAM footprint (host agent, client, server) | **Measured** for agent + fleet server 2026-11-22 (table below); client-side in-session footprint not yet measured |
| 6 | File transfer 1 MB / 100 MB / 1 GB + `scp` baseline + resume-after-kill | Partially: 8 MiB up/down measured every run; the three-size ladder, the `scp` same-path baseline, and the kill-at-50% resume test are not yet measured |
| 7 | Tunnel throughput (iperf3, packet-size matrix, direct vs relayed) | Not yet measured — needs iperf3 on both ends and the two-host rig; the mesh path itself is functionally proven (2026-11-21, SHIM-FIX-REPORT §10) but no throughput figure exists yet |
| 8 | Terminal echo latency (LAN + shaped WAN) + roaming reconnect | Partially: exec round-trip measured every run (loopback); shaped-WAN echo and IP-change reconnect are not yet measured |

Network profiles (a)–(e) from §6 (LAN, 25/50/100 ms shaped WAN,
relay-forced) require the second host awake and netem shaping; the
runs below are loopback on one VM and are labelled that way. Loopback
flatters any product — these tables are the floor, not the WAN story.

## Results — 2026-11-22, Simular box `main`, agent + fleet server v1.2.7

Environment: one VM, Ubuntu 24.04.5 LTS (kernel 6.8.0-142), 2 vCPU,
3,883 MB RAM with ~800 MB free at probe time — the box was under
memory pressure, and the transfer figures below are this-VM,
this-day figures. Path: loopback both ways (client, agent and server
on the same VM). Raw output: `benchmarks/2026-11-22/`.

| Measurement | Direct | Via fleet relay |
|---|---|---|
| Status round-trip ×20 (min · median · p95) | 308 · 315 · 528 ms | not re-measured (see note) |
| Exec round-trip ×10 (min · median · p95) | 5 · 8 · 388 ms | not re-measured (see note) |
| File upload throughput (8 MiB, SHA-256 verified) | 7.2 MiB/s | not re-measured (see note) |
| File download throughput (8 MiB) | 12.9 MiB/s | not re-measured (see note) |
| Desktop fps @ quality 60 | 0.8 — idle static desktop on this VM's scrot+convert capture path; frame-diffing sends almost nothing when nothing changes. Not a protocol ceiling | not re-measured (see note) |
| Cost of knowing the screen (`eso see`) | 633 bytes (≈158 tokens) | 633 bytes (2026-11-18) |
| Inventory delta efficiency | 2,660 B full → 212 B delta (92% less) | 2,345 B full → 376 B delta (84% less, 2026-11-18) |
| Queue enqueue latency | n/a in bench (needs an offline approved device) — the full offline→delivered cycle was measured in the v1.1 live run (BUILD-REPORT §10) | same |

**Relay note (2026-11-22):** the relay column could not be re-run from
the box-local CLI. Since the owner paired the desktop App on
2026-11-20, the fleet server's trusted operator key is the App's key
on the owner's laptop; the box CLI's older operator key gets
`401 Operator signature invalid` — the signed-command model working
as designed (the raw reply is in `benchmarks/2026-11-22/bench-relay.txt`).
The relay figures therefore stand at their last measurement
(2026-11-18, v1.2.0, next table) until a run from the owner's laptop.

**Transfer note:** upload/download on this VM dropped against
2026-11-18 (21.7 / 102.3 MiB/s). The measured difference between the
two runs is the box itself: ~800 MB free RAM and an 11-day uptime
under a desktop session, vs the fresher box of the earlier run. Both
runs are published as measured; neither is adjusted.

## Results — 2026-11-18, Simular box `main`, v1.2.0

`eso bench` direct and `eso bench --device 8AY7-DDZ1` via the fleet
relay; raw output in BUILD-REPORT §13. Same VM class as above,
loopback both ways.

| Measurement | Direct | Via fleet relay |
|---|---|---|
| Status round-trip ×20 (min · median · p95) | 307 · 311 · 503 ms | 314 · 329 · 652 ms |
| Exec round-trip ×10 (min · median · p95) | 4 · 5 · 42 ms | 10 · 14 · 29 ms |
| File upload throughput (8 MiB, SHA-256 verified) | 21.7 MiB/s | 4.5 MiB/s |
| File download throughput (8 MiB) | 102.3 MiB/s | 11.7 MiB/s |
| Desktop fps @ quality 60 | 1.8 — re-measured 2026-11-18 after the §14 bench fix, with the desktop kept changing (mouse jitter); the §13 run read 0.0 on the idle static desktop (frame-diffing sends nothing when nothing changes). Both figures are this VM's scrot+convert capture path, not a protocol ceiling | 2.6 — re-measured 2026-11-18 after the §14 bench fix (the bench hello now offers the App's codec list, `["jpeg","rawdiff"]`); in §13 this cell was "not measured" because the bench's fleet hello omitted the codec list and negotiated no codec |
| Cost of knowing the screen (`eso see`) | 665 bytes (≈166 tokens) | 633 bytes (≈158 tokens) |
| Inventory delta efficiency | 2,345 B full → 205 B delta (91% less) | 2,345 B full → 376 B delta (84% less) |

Cost-of-knowing-the-screen, honestly scoped: on this box the `see`
glance can only report the **absence** of an interactive user session
(the box's desktop session is not visible to the agent's session
discovery — BUILD-REPORT §8), so the ~650 B buys a truthful
"no session" answer, not screen content. The byte comparison still
stands as measured on this same box: a tree answer is ~650 B where
one full pixel frame of the same desktop measured 49,818 B (JPEG,
1280×800, §8) — ~75× smaller. The glance checklist against a real
user session awaits the laptop install.

## Footprint — agent and server at idle (2026-11-22)

Measured on the box above (`VmRSS`, three samples over ~1 minute, no
sessions active; idle CPU over a 15 s window). The two server-side
rows the field has published numbers for are cited next to ours —
different rigs, labelled as such; §6's same-VM server comparison runs
when the rig is assembled.

| Process | Idle RSS | Idle CPU |
|---|---|---|
| EsoAgent agent (Linux, self-contained .NET) | 247,988–262,228 KB (~242–256 MiB) | ≈0.1% of one core |
| EsoAgent fleet server | 189,692–193,596 KB (~185–189 MiB) | ≈0.1% of one core |
| RustDesk hbbs+hbbr (both daemons) | 14.5 MiB — [cited community measurement, ComputingForGeeks, from their same-VM comparison; different rig] | — |
| MeshCentral server | 284 MiB — [cited community measurement, ComputingForGeeks; different rig] | — |

Stated plainly: our agent's idle footprint is the self-contained
.NET runtime plus its heap, and it is not the smallest row in this
table. The RustDesk daemons do far less (no desktop, no perception,
no management plane in the OSS build). Footprint comes down with the
installer-diet work (backlog #5), and this row will be re-measured
when it lands.

## Download size (2026-11-22, official sources)

Bytes served by each vendor's official download URL (HEAD request or
release-asset listing), measured 2026-11-22. This is §6 row 1's first
half; install time and time-to-first-session are not yet measured.

| Product | Download | Bytes |
|---|---|---|
| AnyDesk | AnyDesk.exe (portable, no install) | 8,554,424 |
| RustDesk 1.5.3 | rustdesk-1.5.3-x86_64.exe | 26,233,712 |
| TeamViewer | TeamViewer_Setup_x64.exe (full setup) | 86,399,168 |
| Tailscale | tailscale-setup-latest.exe | 1,407,296 — a stub; the full payload is fetched during install, so this byte count is not comparable to the full installers above |
| EsoAgent | Windows installer (v1.2.3) | 124,427,842 |
| EsoAgent | Linux agent .deb (v1.2.4) | 32,833,564 |

Our installer is the largest honest row in the table — one installer
with zero prerequisites and the whole platform inside, against
AnyDesk's single portable session executable. The diet toward
<60 MB is backlog #5; the row gets re-measured when it ships.

## Mesh path (WireGuard, embedded engine)

- Handshake and end-to-end data plane **proven live 2026-11-21**
  (laptop 10.77.0.3 ↔ box 10.77.0.2, bidirectional payload,
  SHIM-FIX-REPORT §4 and §10) after the apply-order/shim fix in
  commit `26aae13`.
- Throughput and latency over the mesh: **not yet measured.** The
  method is §6 row 7 (iperf3 through the tunnel, packet-size matrix,
  direct vs relayed as separate rows) and row 2 (connect time). The
  2026-11-22 run found the laptop asleep (last handshake 17 h 47 m
  before the probe), so no figure is published here — the mesh rows
  wait for the two-host run.

## Efficiency — the cost of knowing

EsoAgent's efficiency claim is specific and measurable: **the operator
pays bytes (≈ tokens) for knowledge, and structure is cheaper than
pixels while being more accurate.**

- A `see` answer (UI trees + glance) carries the screen's *text
  exactly* — window titles, control labels, the focused element, the
  dialog's wording — at a small fraction of a pixel capture's bytes,
  and it answers the human-glance checklist (what's open / focused /
  shown / changed / where's the error) that a screenshot can only
  suggest.
- Delta perception compounds this: a repeated look costs a diff, not
  a dump. In the runs above, a no-change inventory re-look cost
  ~210 B against a ~2,660 B full payload.
- Intent operations replace 10–20 primitive round-trips with one
  verified call and a compact proof object.
- Desktop streaming adapts to bandwidth but never below the stated
  legibility floor (**quality 40 / 4 fps**); stream bytes/minute at
  the floor is the honest worst-case cost of watching, and the stream
  *reports* when even the floor cannot be carried instead of
  degrading silently.

Sandbox reference points (agent + driver on one VM, loopback,
measured during development — useful as a floor, not as deployment
numbers):

| Measurement | Result |
|---|---|
| Inventory full payload | 5,884 bytes |
| Inventory delta (no changes) | 132 bytes (97.8% smaller) |
| Inventory delta (minor changes) | 208–489 bytes |
| `eso see` with no user session (honest empty) | ~665 bytes |

## Reading these numbers honestly

- Loopback and LAN numbers flatter any product; WAN/relay numbers are
  the ones that matter for a fleet, and they depend on the owner's own
  server placement. Every table here names its path.
- Desktop fps depends on the device's screen content and encoder path
  (JPEG + region-diff today; hardware H.264 is on the roadmap).
- Comparisons with other tools are only valid on the same machines,
  the same network, and the same workload — which is exactly what
  the §6 protocol is for, and why the un-run rows above say so
  instead of borrowing anyone else's numbers.
