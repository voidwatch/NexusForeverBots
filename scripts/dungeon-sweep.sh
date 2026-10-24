#!/usr/bin/env bash
# Runs a bot team through every dungeon, expedition, adventure and raid world that has spawn data, one after the other, and writes
# soak/dungeon-sweep/<time>/summary.txt (entered, cleared, wipes, boss kills, timeouts, missing spell names per world).
#   scripts/dungeon-sweep.sh [dungeon_minutes] [raid_minutes]
# Needs the world database loaded (spawns) and the research data; bots are immortal unless IMMORTAL=0.
set -uo pipefail
cd "$(dirname "$0")/.."
dmin="${1:-12}"; rmin="${2:-25}"
out="soak/dungeon-sweep/$(date +%Y%m%d-%H%M)"
mkdir -p "$out"
set -a; . ./.env; set +a
spawn_count() { docker compose exec -T db mariadb -u"$DB_USER" -p"$DB_PASSWORD" nexus_forever_world -N -e "select count(*) from entity where world=$1" 2>/dev/null; }
# world:team size
worlds="382:5 1263:5 1271:5 1336:5 2980:5 3009:5 3173:5 3522:5 3404:5 1627:5 2183:5 2188:5 2149:5 3180:5 1232:5 1319:5 1149:5 1181:5 1233:5 1323:5 1393:5 1437:5 3010:5 3176:5 1333:20 1462:20 3032:20 3040:20 3102:40"
for entry in $worlds; do
  world="${entry%%:*}"; team="${entry##*:}"
  n="$(spawn_count "$world")"
  if [ "${n:-0}" -lt 20 ]; then echo "world $world: only ${n:-0} spawns, skipped" | tee -a "$out/summary.txt"; continue; fi
  minutes=$dmin; [ "$team" -gt 5 ] && minutes=$rmin
  echo "== world $world team $team ($n spawns), $minutes minutes" | tee -a "$out/summary.txt"
  IMMORTAL="${IMMORTAL:-1}" scripts/dungeon-check.sh "$world" "$team" "$team" "$((minutes * 60))" 12 > "$out/$world.txt" 2>&1
  grep -E '^(  [0-9]+x .*)$|^== ' "$out/$world.txt" | awk '/^== (dungeon-entered|dungeon-cleared|dungeon-wipe|dungeon-timeout|dungeon-enter-failed|encounter-boss-killed|encounter-spell-unresolved|encounter-add-unresolved|encounter-cast-exception|map-update)/{p=1; print; next} /^== /{p=0} p' | head -40 | tee -a "$out/summary.txt"
done
echo "done, details in $out" | tee -a "$out/summary.txt"
