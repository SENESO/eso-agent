#!/usr/bin/env bash
# EsoAgent update applier (Linux). Invoked DETACHED by the agent's UpdateService:
#   update-apply.sh <extractedPackageDir> <appDir>
# Stops the service, swaps the binary keeping a timestamped backup, restarts,
# health-checks /api/status on loopback (any HTTP response = up), rolls back on failure.
set -uo pipefail

EXTRACTED="$1"
APP_DIR="$2"
LOG="$APP_DIR/update-apply.log"
log() { echo "$(date -Iseconds) $*" >> "$LOG"; }
log "apply started: $EXTRACTED -> $APP_DIR"

BACKUP="$APP_DIR/backup-$(date +%Y%m%d%H%M%S)"
mkdir -p "$BACKUP"

systemctl stop esoagent.service || true
sleep 1

[[ -f "$APP_DIR/EsoAgent.Linux" ]] && cp "$APP_DIR/EsoAgent.Linux" "$BACKUP/"
if [[ -f "$EXTRACTED/EsoAgent.Linux" ]]; then
  install -m 0755 "$EXTRACTED/EsoAgent.Linux" "$APP_DIR/EsoAgent.Linux"
fi

systemctl start esoagent.service || true

PORT=8737
if [[ -f /etc/esoagent/esoagent.json ]]; then
  P="$(grep -oP '"port"\s*:\s*\K[0-9]+' /etc/esoagent/esoagent.json | head -1 || true)"
  [[ -n "$P" ]] && PORT="$P"
fi

healthy=0
for _ in $(seq 1 30); do
  sleep 1
  if curl -sk -o /dev/null "https://127.0.0.1:$PORT/api/status"; then healthy=1; break; fi
done

if [[ $healthy -eq 1 ]]; then
  log "health check OK — update applied; backup kept at $BACKUP"
else
  log "health check FAILED — rolling back from $BACKUP"
  systemctl stop esoagent.service || true
  sleep 1
  [[ -f "$BACKUP/EsoAgent.Linux" ]] && install -m 0755 "$BACKUP/EsoAgent.Linux" "$APP_DIR/EsoAgent.Linux"
  systemctl start esoagent.service || true
  log "rollback complete"
fi
