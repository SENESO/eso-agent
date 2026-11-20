# Mesh Embed — implementation report

**Date:** 2026-11-20 · **Spec:** `MESH-EMBED-SPIKE.md` (route A) · **Owner law:**
the final product needs NOTHING external — a customer must never be told to
install WireGuard separately; the tunnel ships inside our own installer.

**Result: implemented.** On Windows the mesh tunnel is now the bundled
wireguard-go engine (`eso-mesh.exe`, MIT, built by us from pinned source)
creating its adapter through the official prebuilt `wintun.dll`, supervised
and configured by the agent over the WireGuard UAPI protocol. The
`wireguard.exe /installtunnelservice` dependency — the only external one the
spike found — is **deleted**: there is one code path, the embedded one, and
the official WireGuard app is ignored even if present.

---

## 1. What was built (per file)

### New

| File | Contents |
|---|---|
| `src/EsoAgent.Core/MeshEngine.cs` | The engine seam. `WgInterfaceConfig`/`WgPeerConfig` (structured config model); `WgConfigParser` (wg-quick text → model); `UapiProtocol` (model → `set=1` request with hex keys + full peer replacement; `get=1` response → typed `UapiDeviceStatus` with per-peer handshake/tx/rx); `IUapiTransport` with `NamedPipeUapiTransport` (Windows: `\\.\pipe\ProtectedPrefix\Administrators\WireGuard\<iface>`) and `UnixSocketUapiTransport` (Linux: `/var/run/wireguard/<iface>.sock` — the follow-up seam); `WindowsMeshAddressing` (pure netsh plan: adapter address + per-AllowedIP routes, delete-before-add for idempotence, mirrored removal plan); `WintunAdapter` (teardown cleanup via wintun.dll's Permitted API: Open/Delete/CloseAdapter); `KillOnCloseJob` (Windows job object, KILL_ON_JOB_CLOSE, so the engine can never outlive the agent). |
| `src/EsoAgent.Core/MeshEngineSupervisor.cs` | The lifecycle state machine (`Stopped/Starting/Running/Restarting/GaveUp/Stopping`). `IMeshEngineProcess` abstraction + `SystemMeshEngineProcess` (spawns `eso-mesh.exe eso0`, output appended to `logs/mesh-engine.log` in the data dir, kill-on-close job assignment). Supervisor: start-or-**adopt** (an engine left by a previous agent lifetime that still answers UAPI is adopted, reconfigured and monitored — never fought for the adapter name), UAPI configure, addressing apply, 15 s health cadence with restart per the shared `RestartPolicy` (budget 10, then `GaveUp` + audit), full teardown (process → adopted-process kill by exact install-path match → route/address removal → Wintun adapter delete). All steps audit-logged via the applier's audit sink. |
| `tests/EsoAgent.Tests/MeshEngineTests.cs` | 19 tests: model↔text byte-parity + parser round-trip + exit-node model; UAPI key hex conversion (incl. rejection of bad keys), set-request shape, get-response parsing against the real wireguard-go field layout, errno handling; addressing plan (dedupe / own-IP skip / normalization / mirror removal); supervisor lifecycle on fakes (spawn+configure+address, honest missing-binary failure, adoption, crash→restart+reconfigure, restart-budget give-up, stop teardown); a live unix-socket round-trip of `UnixSocketUapiTransport` against a fake engine. |
| `scripts/build-mesh-engine.sh` / `.ps1` | Reproducible sidecar build: clone wireguard-go, checkout the pinned commit, **refuse to build if HEAD ≠ pin**, `CGO_ENABLED=0 GOOS=windows GOARCH=amd64 go build -trimpath -buildvcs=false`, stage `eso-mesh.exe` + MIT license + `eso-mesh-provenance.txt` (source repo/tag/commit, Go version, build time, binary SHA-256). |
| `scripts/fetch-wintun.sh` / `.ps1` | Fetch the official `wintun-0.14.1.zip`, verify the zip SHA-256 **and** the extracted amd64 `wintun.dll` SHA-256 against the values pinned in `third-party/README.md`, stage the DLL **unmodified** + its license. Any mismatch fails the build. |
| `third-party/README.md`, `third-party/wintun/LICENSE.txt`, `third-party/wireguard-go/LICENSE` | The license/provenance record: what each component is, where it comes from, its license, and the pinned hashes. |

### Modified

| File | Change |
|---|---|
| `src/EsoAgent.Core/MeshServices.cs` | `WgConfigBuilder` refactored onto the model: `BuildModel(...)` computes the config once; `Build(...)` = `RenderText(BuildModel(...))` — byte-identical output (proven by a parity test), so the wg-quick text and the UAPI configuration can never drift. |
| `src/EsoAgent.Core/MeshApplier.cs` | Windows branch of `ApplyTunnelAsync` now takes the model: renders `eso0.conf` into the data dir for inspection (as before), then hands the model to `MeshEngineSupervisor.ApplyAsync`. The `wireguard.exe` hunt and the `C:\Program Files\WireGuard` fallback are gone. New: when a mesh frame arrives with mesh **disabled** in config, a running engine is torn down instead of left behind. Linux branch unchanged (still wg-quick). Status strings now report engine state + handshake/tx/rx summary instead of "WireGuard for Windows not installed". |
| `src/EsoAgent.Core/MeshProtocol.cs` | `MeshOptions.EnginePath` (optional override for the sidecar path; null = next to the agent exe, the installer contract). Local config only — the fleet wire protocol is untouched. |
| `scripts/publish.sh` / `scripts/publish.ps1` | After the Windows publish, stage the engine + wintun + licenses into `pkg-windows`. Failure aborts the publish — a Windows package without the engine would silently reintroduce the external dependency, so it cannot ship. |
| `.github/workflows/release.yml` | `build-windows` job: `actions/setup-go` (1.24.x) added; the payload step runs both mesh scripts into `payload/` before the zip and the Inno build, so the installer and the zip both carry the engine. A failure fails the release. |
| `installer/windows/EsoAgent.iss` | Comment only: the `[Files]` payload copy already stages `eso-mesh.exe`, `wintun.dll` and `licenses\` into `{app}` recursively — no functional change needed. |

### Untouched (per scope)
The App project (Phase 2), the Phase 0+1 web assets/panels, the
single-instance files, the fleet protocol, the Linux agent's mesh, and all
version stamps (`Directory.Build.props` stays 1.2.4).

## 2. How the sidecar is produced and verified

- **Source pin:** wireguard-go `v0.0.20250522`, commit
  `b3bc704ef0ee21a36298601a9e1e5ff90377ac52` (the spike's commit). The build
  scripts check out exactly this commit and abort if HEAD differs.
- **This session's verification build** (fresh clone, this sandbox,
  Go 1.24.4): produced a genuine PE32+ Windows x86-64 executable,
  5,006,848 bytes, **SHA-256
  `e4f64fa114af09f9681fdab663a1593b35ef4d993b2ef2ff9cba980922b14d00`** —
  recorded in the staged `licenses/eso-mesh-provenance.txt`. (The hash is
  toolchain-specific by nature; every release build records its own in the
  provenance file that ships with it.)
- **wintun.dll:** zip SHA-256 `07c256185d…f24ef51` and DLL SHA-256
  `e5da8447dc…20dafce` both verified against the spike's values during this
  session's staging run; a tampered-zip negative test confirmed the fetch
  script refuses (exit 1) on mismatch. Note: this sandbox's proxy returned
  an empty reply from www.wintun.net, so the staging run used the spike's
  already-downloaded zip from the script's cache — the script still
  hash-verified it before use; on the release runner the download is direct.
- **Full packaging check:** `scripts/publish.sh` run end-to-end in this
  sandbox — see §4.

## 3. License files shipped in every install

Into `{app}\licenses\` (via the payload):

- `LICENSE-wireguard-go.txt` — wireguard-go's MIT license.
- `LICENSE-wintun.txt` — the WireGuard LLC Prebuilt Binaries License, taken
  from the official distribution zip (byte-identical to the repo copy in
  `third-party/wintun/LICENSE.txt`; verified with `cmp` this session).
- `eso-mesh-provenance.txt` — component, source commit, toolchain, and the
  SHA-256 of the exact `eso-mesh.exe` in that install.

The DLL is shipped unmodified (the license forbids modification; the pinned
hashes are the proof) and is used only via the documented `wintun.h`
Permitted API — by wireguard-go itself, and by the agent's teardown helper.

## 4. Build & test results vs baseline

- `dotnet build EsoAgent.sln` (sandbox SDK 8.0.425): **0 warnings, 0 errors**.
- Test suite (xunit console runner, per the sandbox workaround):
  baseline this session **184 total / 1 failure**
  (`EfficiencyTests.PackageVersion_StampRoundTrip` — the pre-existing stamp
  artifact: `Directory.Build.props` is 1.2.4 while `AssemblyVersion` remains
  1.2.1.0; one of the four known sandbox-environment failures named in the
  task baseline — the other three passed in today's environment both before
  and after).
  After: **203 total / 1 failure** — the failing set is name-for-name the
  baseline's; **all 19 new tests pass**, no new failures.
- Packaging: `scripts/publish.sh` run end-to-end in this sandbox (exit 0).
  The mesh stage built the engine (SHA-256 identical to the standalone
  verification build — same pinned source + toolchain ⇒ same binary) and
  hash-verified wintun. Final `dist/esoagent-windows.zip` contains, next to
  the agent executables: `eso-mesh.exe` (5,006,848 B,
  `e4f64fa114af09f9…`), `wintun.dll` (427,552 B, `e5da8447dc2c320e…` — the
  pinned official bytes), and `licenses/` with both license texts and the
  provenance file.

## 5. Linux parity (follow-up, deliberately not done here)

The Linux agent's mesh behavior is **unchanged**: it still drives the kernel
tunnel with `wg-quick` (which is why the box needed wireguard-tools installed
by hand — the sibling defect the spike named). Nothing about the Linux path
was modified in this task.

The structure is ready for the follow-up that retires it:

- `MeshEngineSupervisor` is OS-agnostic except for two injected delegates —
  the addressing plan and adapter cleanup, currently the Windows (netsh /
  Wintun) implementations. A Linux pass supplies an `ip`-command addressing
  plan instead; the lifecycle, UAPI configuration, adoption and restart
  logic carry over verbatim.
- `UnixSocketUapiTransport` already speaks to a Linux engine at
  `/var/run/wireguard/<iface>.sock` and is covered by a live round-trip test.
- `build-mesh-engine.sh` already parameterizes GOOS/GOARCH — a Linux build
  is `--arch` plus a GOOS switch away (the spike proved the Linux binary
  end-to-end on the box: ICMP 3/3, TCP echo, live handshake).

Follow-up in one line: build the sidecar for linux/amd64 in `publish.sh`,
add a Linux addressing plan, point the Linux branch of `ApplyTunnelAsync`
at the supervisor behind the same model, and delete the `wg-quick` branch.

## 6. On-device verification procedure (ship time)

**Acceptance test — on a Windows machine with NO WireGuard app installed,
install EsoAgent → the mesh tunnel comes up → packet proof against the box
peer → a DXGI desktop frame arrives via the fleet.** Precondition check:
`C:\Program Files\WireGuard` absent (ESO's current state).

1. Install the new build. Confirm `{app}` contains `eso-mesh.exe`,
   `wintun.dll` and `licenses\` (3 files), and that no WireGuard application
   exists on the machine.
2. Enroll / enable mesh; wait for a mesh frame. Expect: an adapter named
   `eso0` appears (`Get-NetAdapter`), `eso-mesh.exe` runs as a child of the
   agent service, the engine log (`data\logs\mesh-engine.log`) shows the
   engine start, and the agent's mesh status reports "embedded engine up"
   with a handshake summary instead of "not installed".
3. Packet proof: `ping 10.77.0.2` (the box agent's VIP) from ESO → replies;
   open a TCP connection to the box agent's port over the VIP → connects.
   Status shows a fresh `last_handshake_time` and growing tx/rx.
4. Reboot ESO → the tunnel re-establishes with no user action and no
   external program.
5. Restart the agent service while the tunnel is up → the new lifetime
   either inherits the engine through its kill-on-close job (engine exits
   with the old lifetime and is respawned) or adopts a surviving engine —
   either way the tunnel converges back to the pushed configuration.
6. Disable mesh in config → engine process gone, `eso0` adapter gone,
   routes/address removed. Re-enable → everything returns.
7. Uninstall EsoAgent → no `eso0` adapter, no engine process, no orphaned
   service; `wg.key`/`wg.pub` handled per the existing data-dir policy.
8. Regression: fleet enrollment, owner panel, desktop session and the
   relay-shim path (force a relay peer) behave as before the change.
9. Fleet-level proof (the blocked item this unblocks): from the operator
   side, open a desktop session to the laptop **over the mesh VIP**
   (10.77.0.3) and confirm a live DXGI frame — mesh packet traffic + desktop
   over the embedded tunnel, with zero external installs on the laptop.

### What remains unproven until that on-device run — stated plainly

- **The Windows-only leg has never executed.** Everything Windows-specific
  — Wintun adapter creation by the sidecar under the agent service's
  session, the UAPI named pipe ACL for the service account, the netsh
  addressing plan against a real Wintun adapter, adapter cleanup on
  teardown, and the kill-on-close job behavior — is compiled, unit-tested
  at the seams that can be faked, and built from the spike's proven engine,
  but it has not run on a Windows host yet. This sandbox cannot run it
  (no Windows host; TUN/UDP are blocked here — the same wall the spike hit).
- The spike proved the engine + UAPI seam end-to-end on Linux (packets,
  handshake, TCP). The UAPI wire format, config translation and lifecycle
  logic are therefore evidence-backed; the Windows transport binding
  (named pipe) is new code whose first live exercise is step 2 above.
- wireguard-go's Windows main prints a "test program … for debugging"
  warning to stderr; it is cosmetic, lands only in `mesh-engine.log`, and
  is worth knowing about before reading that log. If it ever matters, the
  kernel route (spike route C: `tunnel.dll` + prebuilt `wireguard.dll`,
  equally license-clean) remains the documented upgrade path.
- arm64 Windows is not covered: the installer is x64-only today, and only
  the amd64 `wintun.dll` is pinned/verified.
