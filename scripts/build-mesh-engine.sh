#!/usr/bin/env bash
# Builds the embedded mesh engine (eso-mesh.exe) from PINNED wireguard-go
# source (MIT) and stages it into a payload directory:
#   <out>/eso-mesh.exe
#   <out>/licenses/LICENSE-wireguard-go.txt
#   <out>/licenses/eso-mesh-provenance.txt   (source commit, Go version, SHA-256)
#
# Usage: build-mesh-engine.sh [--out DIR] [--arch amd64]
# Env:   GO (go binary; default: go on PATH)
#        MESH_BUILD_CACHE (clone/build cache; default: ~/.cache/esoagent-mesh-build)
#
# The build FAILS if the checked-out source is not exactly the pinned commit.
# Provenance: MESH-EMBED-SPIKE.md + third-party/README.md.
set -euo pipefail

PIN_COMMIT="b3bc704ef0ee21a36298601a9e1e5ff90377ac52"
PIN_TAG="v0.0.20250522"
REPO_URLS=("https://git.zx2c4.com/wireguard-go" "https://github.com/WireGuard/wireguard-go")

OUT=""
ARCH="amd64"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --arch) ARCH="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ -n "$OUT" ]] || { echo "usage: build-mesh-engine.sh --out DIR [--arch amd64]" >&2; exit 2; }

# Absolutize OUT before the build subshell changes directory into the source
# clone; a relative --out would otherwise land the exe inside the clone.
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

GO="${GO:-go}"
command -v "$GO" >/dev/null 2>&1 || { echo "go toolchain not found (set GO=/path/to/go)" >&2; exit 1; }
command -v git >/dev/null 2>&1 || { echo "git not found" >&2; exit 1; }

CACHE="${MESH_BUILD_CACHE:-$HOME/.cache/esoagent-mesh-build}"
SRC="$CACHE/wireguard-go"
mkdir -p "$CACHE"

if [[ ! -d "$SRC/.git" ]]; then
  cloned=0
  for url in "${REPO_URLS[@]}"; do
    echo "cloning $url"
    if git clone --quiet "$url" "$SRC"; then cloned=1; break; fi
    rm -rf "$SRC"
  done
  [[ "$cloned" == "1" ]] || { echo "could not clone wireguard-go from any known remote" >&2; exit 1; }
fi

git -C "$SRC" fetch --quiet --tags origin || true
git -C "$SRC" checkout --quiet "$PIN_COMMIT"
HEAD="$(git -C "$SRC" rev-parse HEAD)"
if [[ "$HEAD" != "$PIN_COMMIT" ]]; then
  echo "REFUSING TO BUILD: source is at $HEAD, pinned is $PIN_COMMIT" >&2
  exit 1
fi

mkdir -p "$OUT/licenses"
echo "building eso-mesh.exe (wireguard-go $PIN_TAG @ $PIN_COMMIT, GOOS=windows GOARCH=$ARCH)"
(cd "$SRC" && CGO_ENABLED=0 GOOS=windows GOARCH="$ARCH" \
  "$GO" build -trimpath -buildvcs=false -o "$OUT/eso-mesh.exe" .)

cp "$SRC/LICENSE" "$OUT/licenses/LICENSE-wireguard-go.txt"

SHA="$(sha256sum "$OUT/eso-mesh.exe" | awk '{print $1}')"
GOVER="$("$GO" version)"
{
  echo "eso-mesh.exe - embedded WireGuard mesh engine for EsoAgent"
  echo "component: wireguard-go (userspace WireGuard implementation)"
  echo "license: MIT (see LICENSE-wireguard-go.txt)"
  echo "source: https://git.zx2c4.com/wireguard-go"
  echo "source-tag: $PIN_TAG"
  echo "source-commit: $PIN_COMMIT"
  echo "target: windows/$ARCH"
  echo "go: $GOVER"
  echo "built-at-utc: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "sha256: $SHA"
} > "$OUT/licenses/eso-mesh-provenance.txt"

echo "eso-mesh.exe sha256: $SHA"
echo "staged into $OUT"
