#!/bin/bash
# Regression check for the world 1061 out-of-memory chain. Needs the world running with bots.
# Places 6 Grimwort proxies (52647, Living Grounds summons 52647 on every unit it hits) next to a bot with the self-summon filter OFF,
# waits 5 minutes, turns the filter back on and fails if spell summons or the grid action queue grew past the caps or the heap jumped.
cd "$(dirname "$0")/.."
mem() { docker compose exec -T world sh -c 'grep -A1 "^== memory" /cache/failure-report.txt' </dev/null 2>/dev/null | tail -1; }
before=$(mem | grep -oE '^ *heap [0-9]+' | grep -oE '[0-9]+')
scripts/gt.sh gt spawnnear 52647 6 any unguarded 2>&1 | tail -1 | cut -c1-160
sleep 300
line=$(mem)
scripts/gt.sh gt spawnnear 0 0 any guarded 2>&1 | tail -1
heap=$(echo "$line" | grep -oE '^ *heap [0-9]+' | grep -oE '[0-9]+')
summons=$(echo "$line" | grep -oE 'peak per map [0-9]+' | grep -oE '[0-9]+')
queue=$(echo "$line" | grep -oE 'peak [0-9]+\)' | grep -oE '[0-9]+')
echo "heap ${before:-?} -> ${heap:-?} MB, summon peak per map ${summons:-?}, queue peak ${queue:-?}"
if [ -n "$heap" ] && [ -n "$summons" ] && [ "$summons" -le 400 ] && [ "${queue:-0}" -lt 20000 ] && [ $(( heap - ${before:-$heap} )) -lt 1024 ]; then
  echo "summon-check PASS"; else echo "summon-check FAIL"; exit 1; fi
