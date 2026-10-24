#!/bin/bash
# 10-minute gate with 30 bots; writes /tmp/gate-*.txt for scripts/status.sh and /tmp/gate-done.txt when finished.
#   setsid nohup scripts/gate.sh [bots] </dev/null >/tmp/gate.log 2>&1 &
# Split form for callers that cannot wait 11 minutes in one command:
#   scripts/gate.sh start [bots]   (start the stack and the bots, writes /tmp/gate-start.txt)
#   scripts/gate.sh collect        (after 10 minutes: write the reports and the verdict)
cd "$(dirname "$0")/.."
phase=all
case "$1" in start|collect) phase="$1"; shift;; esac
bots="${1:-30}"
if [ "$phase" != collect ]; then
rm -f /tmp/gate-done.txt /tmp/gate-verdict.txt /tmp/gate-fail.txt /tmp/gate-maps.txt /tmp/gate-stats.txt
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
docker compose up -d >/dev/null 2>&1
sleep 90; scripts/bots.sh count "$bots" >/dev/null 2>&1; date +%s > /tmp/gate-start.txt
[ "$phase" = start ] && exit 0
sleep 600
fi
docker compose exec -T world sh -c 'cat /cache/failure-report.txt' </dev/null > /tmp/gate-fail.txt 2>&1
scripts/gt.sh gt maps > /tmp/gate-maps.txt 2>&1
scripts/cpu.sh > /tmp/gate-stats.txt 2>&1
# verdict: 0 map/spell update exceptions and 0 character save failures are required
ex=$(grep -cE '^== (map-update-exception|spell-update-exception)' /tmp/gate-fail.txt)
cs=$(grep -E '^== character-save-failed ' /tmp/gate-fail.txt | grep -oE '[0-9]+ total' | grep -oE '[0-9]+'); cs=${cs:-0}
# memory: the report's memory line keeps the per-map peaks; a runaway shows as a queue or summon peak or a heap-growth entry
mline=$(grep -A1 '^== memory' /tmp/gate-fail.txt | tail -1)
qpeak=$(echo "$mline" | grep -oE 'peak [0-9]+\)' | head -1 | grep -oE '[0-9]+'); qpeak=${qpeak:-x}
speak=$(echo "$mline" | grep -oE 'peak per map [0-9]+' | grep -oE '[0-9]+'); speak=${speak:-x}
hg=$(grep -cE '^== heap-growth ' /tmp/gate-fail.txt)
vital=$(docker compose logs --since "$(date -u -d @$(( $(cat /tmp/gate-start.txt) - 120 )) +%Y-%m-%dT%H:%M:%SZ)" world 2>/dev/null | grep -c 'for Vital!')
{ [ "$ex" = 0 ] && echo "exceptions PASS" || echo "exceptions FAIL ($ex sections)"
  [ "$cs" = 0 ] && echo "character-saves PASS" || echo "character-saves FAIL ($cs)"
  if [ "$qpeak" != x ] && [ "$qpeak" -lt 20000 ] && [ "$speak" != x ] && [ "$speak" -le 400 ] && [ "$hg" = 0 ]; then echo "memory PASS (queue peak $qpeak, summon peak $speak)"
  else echo "memory FAIL (queue peak $qpeak, summon peak $speak, heap-growth $hg)"; fi
  [ "$vital" = 0 ] && echo "vital-log PASS" || echo "vital-log FAIL ($vital lines)"; } > /tmp/gate-verdict.txt
touch /tmp/gate-done.txt
