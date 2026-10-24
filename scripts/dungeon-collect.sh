#!/usr/bin/env bash
# Collect a run started by scripts/dungeon-start.sh into OUTDIR/WORLD.txt and OUTDIR/summary.txt: scripts/dungeon-collect.sh OUTDIR WORLD TEAM
set -uo pipefail
cd "$(dirname "$0")/.."
out="$1"; world="$2"; team="$3"; mkdir -p "$out"
start=$(cat /tmp/dungeon-start-$world 2>/dev/null || date +%s); min=$(( ($(date +%s) - start) / 60 ))
{
  docker compose ps world | grep -q healthy || echo "WORLD NOT HEALTHY"
  scripts/gt.sh gt instances | head -12
  docker compose exec -T world sh -c 'grep -E "^== (encounter|dungeon|bot-failed|map-update|spell-update|loot-boss)" -A12 /cache/failure-report.txt' </dev/null | head -160
} > "$out/$world.txt" 2>&1
docker compose exec -T world sh -c "cat /cache/failure-report.txt" </dev/null > "$out/$world-full.txt" 2>&1
echo "== world $world team $team, $min minutes" >> "$out/summary.txt"
grep -E '^(  [0-9]+x .*)$|^== ' "$out/$world.txt" | awk '/^== (dungeon-entered|dungeon-cleared|dungeon-wipe|dungeon-timeout|dungeon-enter-failed|encounter-boss-killed|encounter-killed|encounter-spell-unresolved|encounter-add-unresolved|encounter-cast-exception|map-update|spell-update)/{p=1; print; next} /^== /{p=0} p' | head -40 >> "$out/summary.txt"
awk -v w="$world" '$0 ~ "^== world "w" "{p=1} p' "$out/summary.txt" | cut -c1-180 | head -25
