# Mesh Embed Spike — making the Windows mesh self-contained

**Date:** 2026-11-20 · **Scope:** spike only — no product code was modified.
Prototype lives in `~/workspace/.mesh-spike/` (this VM) and `/workspace/mesh-spike/` (Simular box).

## Recommendation (one line)

**Ship the tunnel inside our own installer as a wireguard-go engine sidecar (MIT, built
by us from pinned source) + the official prebuilt `wintun.dll` (WireGuard LLC Prebuilt
Binaries License), driven by the agent over the WireGuard UAPI protocol — prototyped
end-to-end with packets passing; no second program install, no GPL component distributed.**

The official embeddable `tunnel.dll` + prebuilt `wireguard.dll` (WireGuardNT) stack is
equally license-clean and is documented below as the performance upgrade path.

---

## 1. Current implementation — the dependency map

Everything except the tunnel data plane is already ours, in `src/EsoAgent.Core`:

| Function | Where | Ours? |
|---|---|---|
| WG keypair generation (X25519) | `WgCrypto.cs`, persisted by `WgIdentity` (`wg.key`/`wg.pub` in the data dir) | ✅ managed code |
| Config generation (wg-quick format: PrivateKey / Address / ListenPort / peers / AllowedIPs / Endpoint / PersistentKeepalive=25; relay peers point at `127.0.0.1:<RelayShimBasePort+n>`) | `WgConfigBuilder.Build` in `MeshServices.cs` | ✅ |
| Mesh state application, hosts-file sync (MagicDNS-lite), firewall plan, relay shims, exit-node handling | `MeshApplier.ApplyAsync` | ✅ |
| **Creating the actual tunnel on Windows** | `MeshApplier.ApplyTunnelAsync` (Windows branch) | ❌ **shells out to `wireguard.exe /installtunnelservice <eso0.conf>`** |

The exact failure (MeshApplier.cs:168):

```csharp
var wgExe = FindOnPath("wireguard.exe");
if (wgExe == null) return "tunnel: WireGuard for Windows not installed (config generated only)";
```

`FindOnPath` looks on PATH and at `C:\Program Files\WireGuard\wireguard.exe` — i.e. the
**only** missing piece on Windows is the WireGuard for Windows application's tunnel
service (which in turn brings the WireGuardNT kernel driver). Nothing else is external.

**The seam is one method:** `ApplyTunnelAsync(string configText)` — everything upstream
(config, identity, firewall, shims) is engine-agnostic and stays untouched. An embedded
engine attaches exactly there.

Sibling defect (same class, out of this spike's Windows scope): the Linux branch shells
out to `wg-quick` (MeshApplier.cs:178). The box works only because wireguard-tools was
installed on it by hand. The recommended engine also runs on Linux, so one sidecar can
close both branches later.

## 2. License evidence (verified from the actual sources/artifacts, 2026-11-20)

### 2.1 wireguard-go — **MIT** ✅

Source: https://git.zx2c4.com/wireguard-go/plain/LICENSE — the file is the verbatim MIT
grant ("Permission is hereby granted, free of charge, to any person obtaining a copy of
this software … to deal in the Software without restriction …"). This is the reference
userspace WireGuard implementation; on Windows it creates its TUN through Wintun
(its `go.mod` requires `golang.zx2c4.com/wintun`; `tun/tun_windows.go` calls
`wintun.CreateAdapter(...)`).

### 2.2 Wintun — source GPL-2.0, **prebuilt DLL under the "Prebuilt Binaries License"** ✅ (with the exact carve-out we need)

- Source repo `COPYING` (https://git.zx2c4.com/wintun/plain/COPYING) is verbatim GPL-2.0 —
  we do **not** build or distribute Wintun source.
- The official binary distribution `wintun-0.14.1.zip` (https://www.wintun.net/builds/wintun-0.14.1.zip,
  SHA-256 `07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51`) contains
  `LICENSE.txt` = **Prebuilt Binaries License** (WireGuard LLC). Operative clause 3(d):
  redistribution is forbidden *"without the prior written consent of WireGuard LLC,
  **except insofar as the Software is distributed alongside other software that uses the
  Software only via the Permitted API**"* (the `wintun.h` API). Clause 3(a)/(b): the DLL
  must not be modified or reverse-engineered. The project's own README states the
  intended deployment: *"Install the `wintun.dll` file side-by-side with your application."*
  Our use — unmodified DLL next to our binaries, used only via the documented API — is
  precisely the permitted case. No copyleft, no source-disclosure duty, no fee.

### 2.3 BoringTun (Cloudflare) — **BSD-3-Clause** ✅

Source: https://raw.githubusercontent.com/cloudflare/boringtun/master/LICENSE.md —
verbatim BSD-3 ("Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met: …").

### 2.4 WireGuard for Windows app — app code **MIT**, but the shipped product drags in the GPL driver ⚠️

- The repo `COPYING` (https://git.zx2c4.com/wireguard-windows/plain/COPYING) is, contrary
  to the common assumption, the **MIT** license (Copyright (C) 2018-2026 WireGuard LLC).
  The GPL expectation is *refuted for the app code* — and *confirmed for its driver*:
- Its tunnel service and its official embeddable DLL both run on **WireGuardNT**:
  `embeddable-dll-service/README.md` states `tunnel.dll` *"requires `wireguard.dll`,
  which can be downloaded from the wireguard-nt download server"*.
- **WireGuardNT source is GPL-2.0**: https://git.zx2c4.com/wireguard-nt/plain/COPYING is
  verbatim GPL version 2. Building/shipping the driver from source is refused under our
  permissive-only discipline.
- **But the prebuilt `wireguard.dll` SDK is clean**: `wireguard-nt-1.1.zip`
  (https://download.wireguard.com/wireguard-nt/wireguard-nt-1.1.zip, SHA-256
  `dceb30a9bc4be48cce0f74160fc88a585a2c2627366e8f846fc6658f9038dace`) carries the same
  **Prebuilt Binaries License** as Wintun, with the identical clause-3(d) carve-out for
  software "distributed alongside other software that uses the Software only via the
  Permitted API". The download page itself describes it as *"provided under the
  permissive license contained inside."*

### 2.5 The unavoidable fact

Every credible Windows TUN data plane bottoms out in a WireGuard LLC **Prebuilt
Binaries License** DLL (`wintun.dll` or `wireguard.dll`) — or in GPL source. There is no
MIT/BSD TUN driver for Windows. The Prebuilt Binaries License is the channel WireGuard
LLC created exactly for embedding (it is how commercial VPN clients ship), and it meets
the substance of our discipline — redistribution expressly permitted for our use, no
copyleft, nothing GPL distributed. If that license class were rejected too, the honest
conclusion would be: *no self-contained WireGuard mesh on Windows exists* short of
writing and Microsoft-signing our own kernel driver.

## 3. Routes compared

| Route | Stack | License verdict | Fit / effort |
|---|---|---|---|
| **A — recommended** | wireguard-go sidecar (we build it, MIT) + prebuilt `wintun.dll` | ✅ MIT + Prebuilt Binaries carve-out | Agent spawns/supervises the engine (the agent already has a supervised-process facility), configures it via UAPI — the same protocol `wg.exe` uses; on Windows the UAPI endpoint is the named pipe `\\.\pipe\ProtectedPrefix\Administrators\WireGuard\<iface>` (from `ipc/uapi_windows.go`). Config fields map 1:1 from `WgConfigBuilder` output (incl. relay-shim endpoints `127.0.0.1:<port>`). Agent must add the VIP + routes on the adapter itself (netsh / iphlpapi — it already shells netsh-class commands for the firewall plan). Userspace throughput is ample for a management mesh. **Effort ≈ 2–3 dev-weeks incl. on-device verification.** Bonus: same binary can later replace the Linux `wg-quick` dependency. |
| A′ — in-process variant | BoringTun FFI (BSD-3) + `wintun.dll` | ✅ BSD-3 + Prebuilt Binaries carve-out | No sidecar process, but we must write the whole packet pump (Wintun ring ↔ UDP sockets, endpoint roaming, timer servicing via BoringTun's `update_timers`). Most code, most risk. **Effort ≈ 4–6 dev-weeks.** Fallback if a sidecar ever becomes unacceptable. |
| C — kernel variant | `tunnel.dll` built by us from wireguard-windows (MIT) + prebuilt `wireguard.dll` (WireGuardNT) | ✅ MIT + Prebuilt Binaries carve-out | Smallest seam of all: `tunnel.dll` consumes the **same `.conf` file the agent already writes**, via a registered `WireGuardTunnel$eso0` service calling our exe's `/service` entry (a C# sample ships in the repo). Kernel data plane = best throughput. Costs: driver loads at runtime, we must reproduce the `tunnel.dll` build in CI, kernel-path debugging is harder. **Effort ≈ 1–2 dev-weeks.** Documented upgrade path if mesh throughput ever matters (e.g. bulk file transfer over the mesh). |
| B — bundle the official WireGuard installer as a silent prerequisite | consumer MSI inside our Inno setup | ❌ **Refused** | (i) It is literally a second program install — the exact defect the owner ruled out. (ii) License hygiene: the app is MIT, but the package installs the WireGuardNT driver whose source is GPL-2.0, and the consumer installer is not one of the redistribution channels WireGuard LLC offers for embedding (the DLL SDKs are). Under permissive-only discipline the bundle is tainted; under the product ruling it is disqualified anyway. |
| D — write the protocol in C# | — | n/a | **Refused.** No maintained managed WireGuard implementation exists. The product's own `WgCrypto` does key generation only and documents why: its BigInteger X25519 is non-constant-time and *"WireGuard itself does all session crypto."* Session crypto in managed code would be a security regression and the largest effort of all. |

## 4. Prototype — what was actually proven

**Environment note (measured, not assumed):** this VM cannot host the test — UDP
`sendto` returns EPERM and TUN creation returns "operation not permitted" in this
sandbox. The prototype therefore ran on the **Simular box** (Ubuntu 24.04, the same
machine whose kernel mesh the fleet already uses), in scratch namespaces that were
deleted afterwards. The product agent/server on the box were never touched.

**Build (from source, both here and on the box):**

- Go 1.24.4 toolchain (go.dev tarball SHA-256 `77e5da33bb72aeaef1ba4418b6fe511bc4d041873cbf82e5aa6318740df98717`).
- wireguard-go `v0.0.20250522`, commit `b3bc704ef0ee21a36298601a9e1e5ff90377ac52`,
  `CGO_ENABLED=0 go build` → box binary SHA-256 `8acc901c53da97c9c92b7d4aca6d163e3ec7cc1aedecf7375d65e403c1b76576`.

**Topology:** two network namespaces joined by a veth underlay (`10.99.0.1 ↔ 10.99.0.2`),
one wireguard-go instance each (`esoA`, `esoB`), overlay addresses in the mesh range:
**10.77.0.99 ↔ 10.77.0.100**. Both engines were configured **by a .NET 8 program**
(`harness/Program.cs`, ~90 lines) speaking raw UAPI over the engines' unix sockets —
the same control seam the Windows agent will use over the named pipe. Throwaway keys
were generated for the spike only and redacted from the retained logs.

**Results (box, 2026-11-20):**

- UAPI `set` from .NET → `errno=0` on both engines.
- **ICMP:** `ping 10.77.0.100` from the 10.77.0.99 side — **3/3 replies, 0% loss**,
  rtt min/avg/max = 0.364/1.041/1.620 ms.
- **TCP:** a Python echo server on 10.77.0.100 accepted a connection from
  **10.77.0.99:38470** and returned the payload (`pong:hello-tunnel`).
- UAPI `get` (final): both sides show a live handshake
  (`last_handshake_time_sec=1795167375`) and symmetric counters —
  A: tx 1360 / rx 1188 bytes; B: tx 1188 / rx 1212 bytes.
- Raw logs: box `/workspace/mesh-spike/evidence.log` + `evidence2.log` (keys redacted);
  scratch namespaces/processes/sockets were removed after the run.

**Honest boundary:** the engine and the UAPI control seam are *the same code and
protocol* on Windows (wireguard-go's Windows build is in-tree: `main_windows.go`,
`tun/tun_windows.go` → Wintun, `ipc/uapi_windows.go` → named pipe). The Windows-only
leg — `wintun.dll` adapter creation inside ESO — was **not** executed in this spike
(no Windows host available to it) and is step 1 of the verification procedure in §6.

## 5. What changes for the recommended route (A)

**Installer (Inno, `installer/windows/EsoAgent.iss`):**

- The `[Files]` section already copies `payload\*` into `{app}` — so the change is
  payload staging only: add `wintun.dll` (amd64 + arm64, **unmodified**, from the
  official zip, with its `LICENSE.txt`) and `eso-mesh.exe` (our wireguard-go build,
  with the MIT LICENSE and a provenance note: source repo + commit + builder).
  No second installer, no custom actions, no separate uninstall entry — both files
  arrive and leave with `{app}`. First adapter creation loads Wintun's bundled
  Microsoft-signed driver in-process; that is Wintun's documented deployment model.
- Pin both artifacts by SHA-256 in the build pipeline and fail the build on mismatch
  (hashes of the currently verified artifacts are in §2).

**Agent (`EsoAgent.Core` / `EsoAgent.Windows`):**

- Replace the Windows branch of `MeshApplier.ApplyTunnelAsync`: instead of hunting for
  `wireguard.exe`, start/stop the bundled engine (`eso-mesh.exe eso0`, foreground mode)
  under the agent's existing process supervision, then push configuration via a new
  UAPI client (translate `WgConfigBuilder`'s output — or emit UAPI directly from the
  `FleetMeshFrame` — into `set=1` over the named pipe; prototype harness is the seed).
- Apply addressing ourselves: VIP + peer routes (+ exit-node default route when
  designated) on the `eso0` adapter via netsh/iphlpapi; remove them on teardown.
  Relay-shim endpoints (`127.0.0.1:<RelayShimBasePort+n>`) carry over unchanged.
- Status reporting gets *better*: `MeshApplier.Status` can report handshake age and
  tx/rx from UAPI `get` instead of today's binary installed/not-installed. The
  "WireGuard for Windows not installed" string disappears on Windows.
- Identity is untouched: `wg.key`/`wg.pub`, enrollment, frames, firewall plan, hosts
  sync and relay shims all stay exactly as they are.

## 6. On-device verification procedure (implementation time, on ESO)

Precondition: a machine with **no** WireGuard for Windows installed (verify:
`C:\Program Files\WireGuard` absent — this is ESO's current state).

1. Install the new EsoAgent build; confirm `wintun.dll` + `eso-mesh.exe` landed in
   `{app}` and no WireGuard application exists on the machine.
2. Enroll / enable mesh; wait for a mesh frame. Expect: a network adapter named
   `eso0` appears (`Get-NetAdapter`), the engine process is running under the agent's
   supervision, and `MeshApplier.Status` reports a tunnel + handshake instead of
   "not installed".
3. `ping 10.77.0.2` (the box agent's VIP) from ESO → replies; open a TCP connection to
   the box agent's port over the VIP → connects. Check UAPI `get` (via the agent's
   status surface): fresh `last_handshake_time`, growing tx/rx.
4. Reboot ESO → tunnel re-establishes without any user action or external program.
5. Disable mesh in config → adapter and engine go away cleanly. Re-enable → back.
6. Uninstall EsoAgent → no `eso0` adapter, no engine process, no orphaned service;
   `wg.key`/`wg.pub` handling per the existing data-dir policy.
7. Regression: fleet enrollment, panel, desktop session and the relay-shim path
   (force a relay peer) all behave as before the change.

## 7. Artifact inventory (spike)

Local (`~/workspace/.mesh-spike/`): `wireguard-go` (locally built binary, SHA-256
`96e890815bd7ab7897d3417c36291e8cea488831a6408c27a16c9ca43ad524fb`), source clone at
commit `b3bc704…`, `downloads/wintun-0.14.1.zip` (SHA-256 in §2.2; amd64 `wintun.dll`
SHA-256 `e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce`),
`downloads/wireguard-nt-1.1.zip` (SHA-256 in §2.4; amd64 `wireguard.dll` SHA-256
`b1b85e072c45d81358be29d94c599dc76652f912be8c0f0a41e2d5d89a6461d3`), Go toolchain.
Box (`/workspace/mesh-spike/`): same source + build, the .NET UAPI harness source,
keygen source, and the redacted evidence logs.
