#!/usr/bin/env bash
# Dungeon run check: BOTS_DUNGEON=world BOTS_TEAM=n in .env, bots = count. Waits, then prints the dungeon/encounter ledger and the loaded instances.
#   scripts/dungeon-check.sh WORLD TEAM_SIZE BOT_COUNT SECONDS
set -uo pipefail
cd "$(dirname "$0")/.."
world="${1:-382}"; team="${2:-5}"; count="${3:-5}"; wait_s="${4:-600}"
sed -i "s/^BOTS_DUNGEON=.*/BOTS_DUNGEON=$world/; s/^BOTS_TEAM=.*/BOTS_TEAM=$team/; s/^BOTS_EVENTS=.*/BOTS_EVENTS=0/; s/^BOTS_DUNGEON_BOTS=.*/BOTS_DUNGEON_BOTS=$count/; s/^BOTS_IMMORTAL=.*/BOTS_IMMORTAL=${IMMORTAL:-0}/" .env
grep -q '^BOTS_TEAM' .env || echo "BOTS_TEAM=$team" >> .env
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
scripts/bots.sh count "$count" >/dev/null 2>&1
until docker compose ps world | grep -q healthy; do sleep 3; done
sleep "$wait_s"
if ! docker compose ps world | grep -q healthy; then echo "WORLD NOT HEALTHY AFTER THE RUN, last log lines:"; docker compose logs world --tail 150 2>&1 | grep -v Telegraph | cut -c1-250; fi
scripts/gt.sh gt instances | head -${5:-10}
echo "Boss scripts and loot by source (kinds = distinct bosses; sourced = timers confirmed by two sources, partial, guide = names from guides/boss mods with assumed timers, generated = researched abilities on assumed timers; docs/ENCOUNTER-SOURCES.md):"
docker compose exec -T world sh -c 'grep -E "^== (encounter-(killed|engaged|definitions)|loot-boss)" /cache/failure-report.txt' | sed 's/^== /  /'
docker compose exec -T world sh -c 'grep -E "^== (encounter|dungeon|bot-failed|map-update)" -A12 /cache/failure-report.txt' | grep -v -E "^== encounter-(engaged|definitions)" | head -${6:-120}
