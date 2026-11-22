#!/usr/bin/env bash
# EsoAgent installer (Linux). Run ONCE as root from the published package folder.
# Installs the daemon as a systemd service, generates the API token + owner password
# under /etc/esoagent, and prints the operator summary (token shown ONCE).
#
# Usage: sudo ./install.sh [--port 8737] [--fleet-url wss://HOST:8740/fleet/connect]
#        [--fleet-fingerprint <sha256>] [--enrollment-key <key>] [--mesh]
# Owner password: set ESOAGENT_OWNER_PASSWORD for unattended installs, else prompted.
set -euo pipefail

PORT=8737
FLEET_URL=""
FLEET_FP=""
ENROLL_KEY=""
MESH=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) PORT="$2"; shift 2 ;;
    --fleet-url) FLEET_URL="$2"; shift 2 ;;
    --fleet-fingerprint) FLEET_FP="$2"; shift 2 ;;
    --enrollment-key) ENROLL_KEY="$2"; shift 2 ;;
    --mesh) MESH=true; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 1; }

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_SRC="$SRC"
[[ -f "$BIN_SRC/EsoAgent.Linux" ]] || BIN_SRC="$SRC/linux"
[[ -f "$BIN_SRC/EsoAgent.Linux" ]] || { echo "EsoAgent.Linux binary not found next to the installer." >&2; exit 1; }

INSTALL_DIR=/usr/local/lib/esoagent
DATA_DIR=/etc/esoagent
mkdir -p "$INSTALL_DIR" "$DATA_DIR"

systemctl stop esoagent.service 2>/dev/null || true
install -m 0755 "$BIN_SRC/EsoAgent.Linux" "$INSTALL_DIR/EsoAgent.Linux"
install -m 0755 "$SRC/update-apply.sh" "$INSTALL_DIR/update-apply.sh" 2>/dev/null || \
  install -m 0755 "$SRC/linux/update-apply.sh" "$INSTALL_DIR/update-apply.sh"

# --- config ---
# PRESERVE an existing config (same rule as install.ps1): an upgrade
# must never wipe a fleet enrollment (fleet.enabled, server URL,
# fingerprint) by reseeding defaults over it - see LAUNCH-REPORT.md
# s.21 for the Windows incident this rule comes from. Defaults are
# written ONLY on a fresh install, when no esoagent.json exists yet.
if [[ -f "$DATA_DIR/esoagent.json" ]]; then
  echo "Existing config preserved (upgrade): $DATA_DIR/esoagent.json - enrollment and settings untouched."
else
  # No --fleet-url given: seed the default hub so a fresh install shows its
  # device code with zero setup. An explicit --fleet-url always wins.
  FLEET_ENABLED=true
  if [[ -z "$FLEET_URL" ]]; then
    FLEET_URL="wss://100.98.165.106:8740/fleet/connect"
    [[ -z "$FLEET_FP" ]] && FLEET_FP="9b5c0bcc778ebd660d3997f1d9b6dfcc0eae678b1340b3f6d8dfcbe1d0e849c9"
  fi
  cat > "$DATA_DIR/esoagent.json" <<EOF
{
  "port": $PORT,
  "fleet": { "enabled": $FLEET_ENABLED, "serverUrl": "$FLEET_URL", "serverFingerprint": "$FLEET_FP", "enrollmentKey": "$ENROLL_KEY" },
  "mesh": { "enabled": $MESH }
}
EOF
  chmod 600 "$DATA_DIR/esoagent.json"
fi

# --- API token (256-bit) ---
if [[ ! -f "$DATA_DIR/token" ]]; then
  head -c 32 /dev/urandom | base64 -w0 > "$DATA_DIR/token"
  echo >> "$DATA_DIR/token"
fi
chmod 600 "$DATA_DIR/token"

# --- owner password ---
if [[ -z "${ESOAGENT_OWNER_PASSWORD:-}" ]]; then
  read -rsp "Set the OWNER PASSWORD for the control panel (min 8 chars): " ESOAGENT_OWNER_PASSWORD
  echo
fi
ESOAGENT_OWNER_PASSWORD="$ESOAGENT_OWNER_PASSWORD" ESOAGENT_HOME="$DATA_DIR" "$INSTALL_DIR/EsoAgent.Linux" set-owner-password
unset ESOAGENT_OWNER_PASSWORD

# --- systemd unit ---
# KillMode=process: processes the agent launches detached SURVIVE agent restarts
# (they are re-parented, not killed with the daemon's cgroup).
cat > /etc/systemd/system/esoagent.service <<EOF
[Unit]
Description=EsoAgent remote administration agent (owner-controlled)
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
ExecStart=$INSTALL_DIR/EsoAgent.Linux --data-dir $DATA_DIR
KillMode=process
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

# --- firewall guidance (rule applied only if ufw is active; otherwise guidance) ---
if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
  ufw allow from 100.64.0.0/10 to any port "$PORT" proto tcp comment "EsoAgent tailnet only" || true
  echo "ufw: allowed $PORT/tcp from 100.64.0.0/10 (tailnet only)."
else
  echo "NOTE: no active ufw detected. Restrict port $PORT to the tailnet subnet with your firewall, e.g.:"
  echo "  iptables -A INPUT -p tcp -s 100.64.0.0/10 --dport $PORT -j ACCEPT"
  echo "The agent additionally binds ONLY to the tailnet interface + loopback."
fi

systemctl daemon-reload
systemctl enable --now esoagent.service
sleep 2

TAILNET_IP="$(ip -4 addr show 2>/dev/null | grep -oP 'inet 100\.\d+\.\d+\.\d+' | awk '{print $2}' | head -1 || true)"
FP=""
[[ -f "$DATA_DIR/cert.pfx.fingerprint" ]] && FP="$(cat "$DATA_DIR/cert.pfx.fingerprint")"

echo ""
echo "================ EsoAgent installed ================"
echo "Service         : esoagent.service (root, auto-start) — $(systemctl is-active esoagent.service || true)"
echo "Data directory  : $DATA_DIR"
echo "Port            : $PORT"
echo "Tailnet IP      : ${TAILNET_IP:-<none detected>}"
echo "Cert fingerprint (pin this in the client config):"
echo "  $FP"
echo "Owner panel     : https://localhost:$PORT/panel/"
echo ""
echo "API TOKEN — shown ONCE. Hand it to the operator via secure entry:"
echo "  $(cat "$DATA_DIR/token")"
echo "Kill switch     : $INSTALL_DIR/EsoAgent.Linux disable | enable   (or the lock file $DATA_DIR/disabled.lock)"
echo "====================================================="
