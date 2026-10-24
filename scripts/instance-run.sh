#!/usr/bin/env bash
# Run several instances at once, one bot team per world (teams take the worlds of NF_BOTS_DUNGEON round robin), then collect per world.
#   scripts/instance-run.sh start "382 1263 1271" 5     # recreates the world with team*worlds dungeon bots (VETERAN=all|off|alternate)
#   scripts/instance-run.sh collect OUTDIR              # failure report -> OUTDIR/<first world>-report.txt, per-world rows -> OUTDIR/instances.tsv
# Restore .env afterwards with scripts/instance-run.sh restore.
set -uo pipefail
cd "$(dirname "$0")/.."
case "${1:-}" in
start)
  worlds="$2"; team="$3"; n=$(( team * $(wc -w <<<"$worlds") ))
  [ -f /tmp/instance-run.env ] || cp .env /tmp/instance-run.env
  list=$(tr ' ' ',' <<<"$worlds")
  grep -q '^BOTS_VETERAN=' .env || echo 'BOTS_VETERAN=alternate' >> .env
  sed -i "s/^BOTS_VETERAN=.*/BOTS_VETERAN=${VETERAN:-alternate}/" .env
  sed -i "s/^BOTS_DUNGEON=.*/BOTS_DUNGEON=$list/; s/^BOTS_TEAM=.*/BOTS_TEAM=$team/; s/^BOTS_EVENTS=.*/BOTS_EVENTS=0/; s/^BOTS_DUNGEON_BOTS=.*/BOTS_DUNGEON_BOTS=$n/; s/^BOTS_SYSTEM_BOTS=.*/BOTS_SYSTEM_BOTS=0/; s/^BOTS_EVENT_BOTS=.*/BOTS_EVENT_BOTS=0/; s/^BOTS_IMMORTAL=.*/BOTS_IMMORTAL=${IMMORTAL:-1}/" .env
  unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
  scripts/bots.sh count "$n" >/dev/null 2>&1
  until docker compose ps world | grep -q healthy; do sleep 3; done
  echo "$worlds|$team|$(date +%s)" > /tmp/instance-run.cur
  echo "worlds $worlds team $team bots $n started $(date +%H:%M)"
  ;;
collect)
  out="$2"; mkdir -p "$out"
  IFS='|' read -r worlds team start < /tmp/instance-run.cur
  min=$(( ($(date +%s) - start) / 60 ))
  tag=$(cut -d' ' -f1 <<<"$worlds")
  docker compose exec -T world sh -c "cat /cache/failure-report.txt" </dev/null > "$out/$tag-report.txt" 2>&1
  docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}}' nexusforeverbots-world-1 > "$out/$tag-stats.txt" 2>&1
  python3 scripts/research/instance_table.py "$out/$tag-report.txt" "$team" "$min" $worlds | tee -a "$out/instances.tsv" | cut -c1-220
  ;;
restore)
  [ -f /tmp/instance-run.env ] && cp /tmp/instance-run.env .env && rm /tmp/instance-run.env && echo restored
  ;;
*) sed -n 2,6p "$0" ;;
esac
