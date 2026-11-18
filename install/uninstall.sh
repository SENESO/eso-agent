#!/usr/bin/env bash
# EsoAgent uninstaller (Linux). Run as root. Removes the service + binaries;
# asks before deleting /etc/esoagent (token, cert, device identity, audit logs).
set -uo pipefail
[[ $EUID -eq 0 ]] || { echo "Run as root (sudo)." >&2; exit 1; }

systemctl disable --now esoagent.service 2>/dev/null || true
rm -f /etc/systemd/system/esoagent.service
systemctl daemon-reload || true
rm -rf /usr/local/lib/esoagent

read -rp "Delete ALL EsoAgent data in /etc/esoagent (token, cert, identity, logs)? [y/N] " answer
if [[ "$answer" == "y" ]]; then
  rm -rf /etc/esoagent
  echo "EsoAgent fully removed (binaries + data)."
else
  echo "EsoAgent service removed; data kept in /etc/esoagent"
fi
