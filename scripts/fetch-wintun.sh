#!/usr/bin/env bash
# Fetches the official prebuilt wintun.dll (WireGuard LLC Prebuilt Binaries
# License) and stages it, UNMODIFIED, into a payload directory:
#   <out>/wintun.dll
#   <out>/licenses/LICENSE-wintun.txt
#
# Usage: fetch-wintun.sh [--out DIR] [--arch amd64]
# Env:   MESH_BUILD_CACHE (download cache; default: ~/.cache/esoagent-mesh-build)
#
# Both the distribution zip and the extracted DLL are verified against the
# SHA-256 values pinned in third-party/README.md; any mismatch fails the
# build. The license forbids modifying the DLL — verification is how we
# prove we ship it byte-for-byte.
set -euo pipefail

VERSION="0.14.1"
ZIP_URL="https://www.wintun.net/builds/wintun-${VERSION}.zip"
ZIP_SHA256="07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51"
DLL_SHA256_AMD64="e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce"

OUT=""
ARCH="amd64"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) OUT="$2"; shift 2 ;;
    --arch) ARCH="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ -n "$OUT" ]] || { echo "usage: fetch-wintun.sh --out DIR [--arch amd64]" >&2; exit 2; }
[[ "$ARCH" == "amd64" ]] || { echo "only amd64 is pinned/verified today (got: $ARCH)" >&2; exit 1; }

CACHE="${MESH_BUILD_CACHE:-$HOME/.cache/esoagent-mesh-build}"
mkdir -p "$CACHE" "$OUT/licenses"
ZIP="$CACHE/wintun-${VERSION}.zip"

if [[ ! -f "$ZIP" ]]; then
  echo "downloading $ZIP_URL"
  curl -fsSL "$ZIP_URL" -o "$ZIP.tmp"
  mv "$ZIP.tmp" "$ZIP"
fi

ACTUAL_ZIP_SHA="$(sha256sum "$ZIP" | awk '{print $1}')"
if [[ "$ACTUAL_ZIP_SHA" != "$ZIP_SHA256" ]]; then
  echo "REFUSING: wintun zip sha256 mismatch" >&2
  echo "  expected $ZIP_SHA256" >&2
  echo "  actual   $ACTUAL_ZIP_SHA" >&2
  exit 1
fi

EXTRACT="$CACHE/wintun-${VERSION}-extract"
rm -rf "$EXTRACT"
mkdir -p "$EXTRACT"
unzip -q "$ZIP" -d "$EXTRACT"

DLL="$EXTRACT/wintun/bin/$ARCH/wintun.dll"
[[ -f "$DLL" ]] || { echo "wintun.dll for $ARCH not found inside the zip" >&2; exit 1; }
ACTUAL_DLL_SHA="$(sha256sum "$DLL" | awk '{print $1}')"
if [[ "$ACTUAL_DLL_SHA" != "$DLL_SHA256_AMD64" ]]; then
  echo "REFUSING: wintun.dll sha256 mismatch" >&2
  echo "  expected $DLL_SHA256_AMD64" >&2
  echo "  actual   $ACTUAL_DLL_SHA" >&2
  exit 1
fi

cp "$DLL" "$OUT/wintun.dll"
cp "$EXTRACT/wintun/LICENSE.txt" "$OUT/licenses/LICENSE-wintun.txt"
echo "wintun.dll $VERSION ($ARCH) sha256 verified: $ACTUAL_DLL_SHA"
echo "staged into $OUT"
