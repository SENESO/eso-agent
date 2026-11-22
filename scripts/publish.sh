#!/usr/bin/env bash
# Builds the distributable packages into dist/:
#   dist/esoagent-windows.zip  — service + tray (win-x64, one shared-runtime folder) + installers
#   dist/esoagent-linux.zip    — daemon (linux-x64, self-contained single-file) + installers
#   dist/esoagent-server.zip   — fleet server (linux-x64 + win-x64)
#   dist/esoagent-client.zip   — operator CLI (linux-x64 + win-x64)
#   dist/esoagent-app.zip      — desktop app (Avalonia GUI, linux-x64 + win-x64)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT="$(pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[[ -x "$DOTNET" ]] || DOTNET="dotnet"
DIST="$ROOT/dist"
rm -rf "$DIST"
mkdir -p "$DIST"

# The ONE version source (item 45): Directory.Build.props.
VERSION="$(grep -oPm1 '(?<=<Version>)[^<]+' "$ROOT/Directory.Build.props")"
echo "== EsoAgent v$VERSION =="

pub() { # project, rid, outdir
  "$DOTNET" publish "$1" -c Release -r "$2" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -p:SatelliteResourceLanguages=en -o "$3" --nologo -v q
}

# Windows payload (installer diet, BUILD-REPORT §19): the service, tray
# (and, in the CI payload, the app) publish NON-single-file into ONE
# folder, so the three shared frameworks they all carry (NETCore,
# AspNetCore, WindowsDesktop) land on disk once instead of once per
# exe. Satellite resources are English-only: the runtime packs ship 14
# cultures, none of them Arabic, and the product's own AR/EN strings
# live in the app, not in framework resources — nothing the product
# uses is lost. The apphost exes keep their names, so install.ps1,
# update-apply.ps1 and the Inno script work unchanged in shape.
pubwin() { # project, outdir
  "$DOTNET" publish "$1" -c Release -r win-x64 --self-contained true \
    -p:DebugType=none -p:SatelliteResourceLanguages=en -o "$2" --nologo -v q
}

# WPF + design-time payload that no shipped code can reach: the tray
# is WinForms-only and no project references WPF (grep-verified across
# src/: no PresentationFramework/PresentationCore/ElementHost/
# System.Windows.Media usage), and System.Windows.Forms.Design.dll
# is the Visual Studio designer assembly, never loaded by a running
# app. ~47 MB of the folder. The list is explicit on purpose — a file
# joins it only with the same evidence.
PRUNE_WIN=(
  PresentationFramework.dll PresentationCore.dll PresentationUI.dll
  ReachFramework.dll System.Windows.Controls.Ribbon.dll
  PresentationNative_cor3.dll wpfgfx_cor3.dll D3DCompiler_47_cor3.dll
  PenImc_cor3.dll UIAutomationClientSideProviders.dll System.Printing.dll
  PresentationFramework.Aero.dll PresentationFramework.Aero2.dll
  PresentationFramework.AeroLite.dll PresentationFramework.Classic.dll
  PresentationFramework.Luna.dll PresentationFramework.Royale.dll
  PresentationFramework-SystemData.dll PresentationFramework-SystemDrawing.dll
  PresentationFramework-SystemXml.dll PresentationFramework-SystemXmlLinq.dll
  System.Windows.Presentation.dll System.Windows.Forms.Design.dll
)
prune_win() { # payload dir
  local f
  for f in "${PRUNE_WIN[@]}"; do rm -f "$1/$f"; done
}

echo "== Windows service + tray (win-x64, shared-runtime folder) =="
pubwin src/EsoAgent.Windows "$DIST/pkg-windows"
pubwin src/EsoAgent.Tray "$DIST/pkg-windows"
prune_win "$DIST/pkg-windows"
cp install/install.ps1 install/uninstall.ps1 install/update-apply.ps1 "$DIST/pkg-windows/"
echo "== Embedded mesh engine (eso-mesh.exe + wintun.dll) =="
# The Windows package must never ship without the embedded engine: a missing
# Go toolchain or a failed hash check aborts the publish (set -e) rather
# than silently reintroducing the external WireGuard dependency.
# GO=/path/to/go overrides the toolchain (default: go on PATH).
bash "$ROOT/scripts/build-mesh-engine.sh" --out "$DIST/pkg-windows"
bash "$ROOT/scripts/fetch-wintun.sh" --out "$DIST/pkg-windows"
echo "$VERSION" > "$DIST/pkg-windows/esoagent-version.txt"
(cd "$DIST/pkg-windows" && zip -q -9 -r "$DIST/esoagent-windows.zip" .)

echo "== Linux daemon (linux-x64) =="
pub src/EsoAgent.Linux linux-x64 "$DIST/pkg-linux"
cp install/install.sh install/uninstall.sh install/update-apply.sh "$DIST/pkg-linux/"
chmod +x "$DIST/pkg-linux/"*.sh
echo "$VERSION" > "$DIST/pkg-linux/esoagent-version.txt"
(cd "$DIST/pkg-linux" && zip -q -9 -r "$DIST/esoagent-linux.zip" .)

echo "== Fleet server (linux-x64 + win-x64) =="
pub src/EsoAgent.Server linux-x64 "$DIST/pkg-server/linux"
pub src/EsoAgent.Server win-x64 "$DIST/pkg-server/windows"
echo "$VERSION" > "$DIST/pkg-server/esoagent-version.txt"
(cd "$DIST/pkg-server" && zip -q -9 -r "$DIST/esoagent-server.zip" .)

echo "== Operator client (linux-x64 + win-x64) =="
pub src/EsoAgent.Client linux-x64 "$DIST/pkg-client/linux"
pub src/EsoAgent.Client win-x64 "$DIST/pkg-client/windows"
echo "$VERSION" > "$DIST/pkg-client/esoagent-version.txt"
(cd "$DIST/pkg-client" && zip -q -9 -r "$DIST/esoagent-client.zip" .)

echo "== Desktop app (linux-x64 + win-x64) =="
pub src/EsoAgent.App linux-x64 "$DIST/pkg-app/linux"
pub src/EsoAgent.App win-x64 "$DIST/pkg-app/windows"
echo "$VERSION" > "$DIST/pkg-app/esoagent-version.txt"
(cd "$DIST/pkg-app" && zip -q -9 -r "$DIST/esoagent-app.zip" .)

echo "== Debian packages (dpkg-deb) =="
if command -v dpkg-deb >/dev/null; then
  bash "$ROOT/scripts/package-deb.sh" agent "$DIST/pkg-linux" "$VERSION" "$DIST"
  bash "$ROOT/scripts/package-deb.sh" server "$DIST/pkg-server/linux" "$VERSION" "$DIST"
else
  echo "dpkg-deb not found — .deb skipped (tarball + install.sh remain the Linux path)"
fi

echo "== Release manifest (signed when the release key is present) =="
python3 "$ROOT/scripts/make-manifest.py" "$DIST" "$VERSION" || echo "manifest generation failed (non-fatal for local builds)"

echo ""
echo "Packages in $DIST:"
ls -la "$DIST"/*.zip "$DIST"/*.deb "$DIST"/manifest.json 2>/dev/null || ls -la "$DIST"/*.zip
