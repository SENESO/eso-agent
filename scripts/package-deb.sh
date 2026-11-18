#!/usr/bin/env bash
# Builds a .deb for the agent or the fleet server from a publish output dir.
#   package-deb.sh agent|server <publish-dir> <version> <out-dir>
# Layout + behaviour mirror install.sh exactly: same install dir, same
# data dir, same systemd unit — a .deb install and an install.sh install
# are THE SAME installation (item 45: never two installs).
# Data policy: `remove` keeps /etc/esoagent(-server); only an explicit
# `purge` deletes it — data removal is always the user's choice.
set -euo pipefail
KIND="$1"; SRC="$2"; VERSION="$3"; OUT="$4"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if [[ "$KIND" == "agent" ]]; then
  PKG="esoagent"; LIB="/usr/local/lib/esoagent"; DATA="/etc/esoagent"; UNIT="esoagent.service"; BIN="EsoAgent.Linux"
  DESC="EsoAgent agent — owner-controlled remote administration (one device)"
else
  PKG="esoagent-server"; LIB="/usr/local/lib/esoagent-server"; DATA="/etc/esoagent-server"; UNIT="esoagent-server.service"; BIN="eso-server"
  DESC="EsoAgent fleet server — device registry, signed relay, owner panel"
fi

mkdir -p "$WORK$LIB" "$WORK/etc/systemd/system" "$WORK/DEBIAN"
chmod 755 "$WORK" "$WORK/DEBIAN"
cp -a "$SRC"/. "$WORK$LIB"/
rm -f "$WORK$LIB/esoagent-version.txt"
chmod +x "$WORK$LIB/$BIN" 2>/dev/null || true

if [[ "$KIND" == "agent" ]]; then
cat > "$WORK/etc/systemd/system/$UNIT" <<UNIT
[Unit]
Description=EsoAgent remote administration agent (owner-controlled)
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=$LIB/$BIN --data-dir $DATA
KillMode=process
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT
else
cat > "$WORK/etc/systemd/system/$UNIT" <<UNIT
[Unit]
Description=EsoAgent fleet server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=$LIB/$BIN --data-dir $DATA
KillMode=process
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT
fi

cat > "$WORK/DEBIAN/control" <<CONTROL
Package: $PKG
Version: $VERSION
Section: admin
Priority: optional
Architecture: amd64
Maintainer: EsoAgent <esoagent@localhost>
Description: $DESC
 Self-contained .NET 8 build — no runtime prerequisite. Managed as a
 systemd service; device data lives under $DATA and survives upgrades.
CONTROL

cat > "$WORK/DEBIAN/postinst" <<'POST'
#!/bin/bash
set -e
DATA_DIR="__DATA__"; UNIT="__UNIT__"
mkdir -p "$DATA_DIR"
chmod 700 "$DATA_DIR" || true
systemctl daemon-reload || true
systemctl enable "$UNIT" || true
systemctl restart "$UNIT" || true
# Post-install verification (same checks as install.sh): the install only
# reports success once the service is actually up.
for i in $(seq 1 15); do
  if systemctl is-active --quiet "$UNIT"; then
    echo "$UNIT is active."
    exit 0
  fi
  sleep 1
done
echo "WARNING: $UNIT did not become active; check: journalctl -u $UNIT" >&2
exit 0
POST
sed -i "s|__DATA__|$DATA|g; s|__UNIT__|$UNIT|g" "$WORK/DEBIAN/postinst"

cat > "$WORK/DEBIAN/prerm" <<'PRE'
#!/bin/bash
set -e
if [ "$1" = "remove" ] || [ "$1" = "upgrade" ]; then
  systemctl stop "__UNIT__" || true
fi
if [ "$1" = "remove" ]; then
  systemctl disable "__UNIT__" || true
fi
exit 0
PRE
sed -i "s|__UNIT__|$UNIT|g" "$WORK/DEBIAN/prerm"

cat > "$WORK/DEBIAN/postrm" <<'POST'
#!/bin/bash
set -e
systemctl daemon-reload || true
if [ "$1" = "purge" ]; then
  # Explicit user choice only: purge removes the device data too.
  rm -rf "__DATA__"
  echo "Purged: device data under __DATA__ was removed."
else
  echo "Device data kept under __DATA__ (use 'dpkg --purge' to remove it)."
fi
exit 0
POST
sed -i "s|__DATA__|$DATA|g" "$WORK/DEBIAN/postrm"
chmod 644 "$WORK/DEBIAN/control"
chmod 755 "$WORK/DEBIAN/postinst" "$WORK/DEBIAN/prerm" "$WORK/DEBIAN/postrm"
find "$WORK$LIB" -type d -exec chmod 755 {} +

dpkg-deb --build --root-owner-group "$WORK" "$OUT/${PKG}_${VERSION}_amd64.deb" >/dev/null
echo "built $OUT/${PKG}_${VERSION}_amd64.deb"
