# Third-party components — the embedded mesh engine

EsoAgent's Windows mesh tunnel is fully self-contained (owner product law,
2026-11-20: the product must never require a separate WireGuard install).
It is built from exactly two third-party components, both staged into the
installer payload next to the agent (`eso-mesh.exe`, `wintun.dll`, and a
`licenses/` folder carrying the files listed here). License analysis:
`MESH-EMBED-SPIKE.md` §2.

## wireguard-go — the tunnel engine (`eso-mesh.exe`)

- What: the reference userspace WireGuard implementation, run as a
  supervised sidecar process and driven by the agent over the UAPI protocol.
- Source: https://git.zx2c4.com/wireguard-go (mirror:
  https://github.com/WireGuard/wireguard-go)
- Pinned: tag `v0.0.20250522`, commit
  `b3bc704ef0ee21a36298601a9e1e5ff90377ac52`
- License: **MIT** — full text in `wireguard-go/LICENSE` in this folder,
  staged into every install as `licenses/LICENSE-wireguard-go.txt`.
- Build: WE build it from the pinned source (never a downloaded binary):
  `scripts/build-mesh-engine.sh` / `scripts/build-mesh-engine.ps1`
  (CGO disabled, GOOS=windows GOARCH=amd64). The build writes
  `licenses/eso-mesh-provenance.txt` into the payload recording the source
  commit, the Go toolchain version, and the SHA-256 of the produced binary.

## Wintun — the Windows TUN driver (`wintun.dll`)

- What: the signed kernel driver wireguard-go creates its adapter through.
  We ship the official prebuilt DLL **unmodified**, side-by-side with the
  engine — Wintun's documented deployment model. We do not build or
  distribute Wintun source (which is GPL-2.0).
- Source of the binary: the official distribution
  https://www.wintun.net/builds/wintun-0.14.1.zip
  - zip SHA-256: `07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51`
  - `bin/amd64/wintun.dll` SHA-256: `e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce`
- License: **WireGuard LLC Prebuilt Binaries License** — full text in
  `wintun/LICENSE.txt` in this folder, staged into every install as
  `licenses/LICENSE-wintun.txt`. Clause 3(d) expressly permits
  redistribution "alongside other software that uses the Software only via
  the Permitted API" — which is exactly this use (the engine talks to the
  DLL through the `wintun.h` API; the agent's teardown helper uses the same
  documented API). The DLL must not be modified — the fetch script verifies
  the SHA-256 above and fails the build on any mismatch.
- Fetch: `scripts/fetch-wintun.sh` / `scripts/fetch-wintun.ps1`.
