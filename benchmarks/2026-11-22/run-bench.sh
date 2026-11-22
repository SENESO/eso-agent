#!/usr/bin/env bash
# Reproduce the 2026-11-22 benchmark run (see BENCHMARKS.md for method).
# Runs on the host that holds the client config (~/.esoagent/client.json).
set -u
ESO="${ESO:-eso}"            # path to the eso client binary
DEVICE="${DEVICE:-}"         # fleet device code for the relay column

echo "== status ==";        $ESO status
echo "== bench (direct) =="; $ESO bench
if [ -n "$DEVICE" ]; then
  echo "== bench (fleet relay, device $DEVICE) =="
  $ESO bench --device "$DEVICE"   # needs the paired operator key; a 401
                                  # here means this client is not the
                                  # fleet operator (by design since the
                                  # 2026-11-20 App pairing)
fi
echo "== see (wire cost of a structural answer) =="; $ESO see | head -2
echo "== idle footprint (3 samples, 25 s apart) =="
for i in 1 2 3; do
  for p in $(pgrep -f 'EsoAgent.Linux --data-dir') $(pgrep -f 'eso-server --data-dir'); do
    printf '%s rss_kb=%s\n' "$(cat /proc/$p/comm)" "$(awk '/VmRSS/{print $2}' /proc/$p/status)"
  done
  [ $i -lt 3 ] && sleep 25
done
