#!/usr/bin/env bash
# Start one bot-team dungeon run and return at once (collect later with scripts/dungeon-collect.sh): scripts/dungeon-start.sh WORLD TEAM
set -uo pipefail
cd "$(dirname "$0")/.."
world="$1"; team="$2"
sed -i "s/^BOTS_DUNGEON=.*/BOTS_DUNGEON=$world/; s/^BOTS_TEAM=.*/BOTS_TEAM=$team/; s/^BOTS_EVENTS=.*/BOTS_EVENTS=0/; s/^BOTS_DUNGEON_BOTS=.*/BOTS_DUNGEON_BOTS=$team/; s/^BOTS_IMMORTAL=.*/BOTS_IMMORTAL=${IMMORTAL:-1}/" .env
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
scripts/bots.sh count "$team" >/dev/null 2>&1
until docker compose ps world | grep -q healthy; do sleep 3; done
date +%s > /tmp/dungeon-start-$world
echo "world $world team $team started $(date +%H:%M)"
