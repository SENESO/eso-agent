# EsoAgent — Benchmarks

Measured numbers only. Every figure in this file comes from running
`eso bench` (or the stated measurement) against a real deployment; the
method is stated with the numbers, and local numbers are labelled local.
No figure is estimated, extrapolated, or copied from another product's
marketing.

## How to reproduce

```
eso bench                 # against the configured device (direct or fleet relay)
eso bench --out BENCHMARKS.md
```

`eso bench` measures, against the target device:

- **Status round-trip** — 20 × `GET /api/status`, min / median / p95.
- **Exec round-trip** — 10 × a trivial shell exec, min / median / p95.
- **File transfer** — an 8 MiB upload (chunked, SHA-256 verified) and the
  same file back down; throughput in MB/s.
- **Desktop frame rate** — frames received in ~5 s at quality 60, view mode.
- **Cost of knowing the screen** — the byte size of a tree-based
  `eso see` answer vs a full pixel capture of the same desktop.
- **Delta efficiency** — a repeat inventory with delta perception: full
  payload bytes vs the structural diff actually sent.
- **Queue enqueue latency** — how fast an offline command is accepted for
  queued delivery. True *delivery* time needs an offline→online cycle and
  is measured in the live test script, not here.

## Results

Numbers are filled in by the bench run against the deployment being
reported. Until a run fills a cell, it says so — an empty cell is honest,
a guessed one would not be.

**Live run — Simular box `main` (agent + fleet server on one VM,
loopback both ways), 2026-11-18, v1.2.0** (`eso bench` direct and
`eso bench --device 8AY7-DDZ1` via the fleet relay; raw output in
BUILD-REPORT §13):

| Measurement | Direct | Via fleet relay |
|---|---|---|
| Status round-trip ×20 (min · median · p95) | 307 · 311 · 503 ms | 314 · 329 · 652 ms |
| Exec round-trip ×10 (min · median · p95) | 4 · 5 · 42 ms | 10 · 14 · 29 ms |
| File upload throughput (8 MiB, SHA-256 verified) | 21.7 MiB/s | 4.5 MiB/s |
| File download throughput (8 MiB) | 102.3 MiB/s | 11.7 MiB/s |
| Desktop fps @ quality 60 | 1.8 — re-measured 2026-11-18 after the §14 bench fix, with the desktop kept changing (mouse jitter); the §13 run read 0.0 on the idle static desktop (frame-diffing sends nothing when nothing changes — and its probe hello also lacked codecs, see relay note) | 2.6 — re-measured 2026-11-18 after the §14 bench fix (the bench hello now offers the App's codec list, `["jpeg","rawdiff"]`); in §13 this cell was "not measured" because the bench's fleet hello omitted the codec list and negotiated no codec. Both figures are this VM's scrot+convert capture path, not a protocol ceiling |
| Cost of knowing the screen (`eso see`) | 665 bytes (≈166 tokens) | 633 bytes (≈158 tokens) |
| Inventory delta efficiency | 2,345 B full → 205 B delta (91% less) | 2,345 B full → 376 B delta (84% less) |
| Queue enqueue latency | n/a in bench (needs an offline approved device) — the full offline→delivered cycle was measured in the v1.1 live run (BUILD-REPORT §10) | same |

Cost-of-knowing-the-screen, honestly scoped: on this box the `see`
glance could only report the **absence** of an interactive user session
(the box's desktop session is not visible to the agent's session
discovery — BUILD-REPORT §8), so the 665 B bought a truthful "no
session" answer, not screen content. The byte comparison still stands
as measured on this same box: a tree answer is ~665 B where one full
pixel frame of the same desktop measured 49,818 B (JPEG, 1280×800,
§8) — ~75× smaller. The glance checklist against a real user session
awaits the laptop install.

Sandbox reference points (agent + driver on one VM, loopback, measured
during development — useful as a floor, not as deployment numbers):

| Measurement | Result |
|---|---|
| Inventory full payload | 5,884 bytes |
| Inventory delta (no changes) | 132 bytes (97.8% smaller) |
| Inventory delta (minor changes) | 208–489 bytes |
| `eso see` with no user session (honest empty) | ~665 bytes |

## Efficiency — the cost of knowing

EsoAgent's efficiency claim is specific and measurable: **the operator
pays bytes (≈ tokens) for knowledge, and structure is cheaper than
pixels while being more accurate.**

- A `see` answer (UI trees + glance) carries the screen's *text exactly*
  — window titles, control labels, the focused element, the dialog's
  wording — at a small fraction of a pixel capture's bytes, and it
  answers the human-glance checklist (what's open / focused / shown /
  changed / where's the error) that a screenshot can only suggest.
- Delta perception compounds this: a repeated look costs a diff, not a
  dump. In the sandbox reference above, a no-change inventory re-look
  cost 132 bytes against a 5,884-byte full payload.
- Intent operations replace 10–20 primitive round-trips with one
  verified call and a compact proof object.
- Desktop streaming adapts to bandwidth but never below the stated
  legibility floor (**quality 40 / 4 fps**); stream bytes/minute at the
  floor is the honest worst-case cost of watching, and the stream
  *reports* when even the floor cannot be carried instead of degrading
  silently.

Agent idle footprint (CPU/RAM at rest, from `eso telemetry` on the
deployment) belongs in the run's notes — measure it on the target, not
here.

## Reading these numbers honestly

- Loopback and LAN numbers flatter any product; WAN/relay numbers are
  the ones that matter for a fleet, and they depend on the owner's own
  server placement.
- Desktop fps depends on the device's screen content and encoder path
  (JPEG + region-diff today; hardware H.264 is on the roadmap).
- Comparisons with other tools are only valid on the same machines, the
  same network, and the same workload — which is exactly what
  `eso bench` is for.
