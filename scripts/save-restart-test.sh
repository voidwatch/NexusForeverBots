#!/usr/bin/env bash
# Character save safety checks (needs the world running with bots that have quests in progress).
#   scripts/save-restart-test.sh restart [item id]   one bot gets an item and a quest objective step, 5 s later the world is stopped
#                                                    (SIGTERM, docker compose stop); the database must hold both (shutdown save).
#   scripts/save-restart-test.sh fault [item id]     one bot gets an item and a step, its character batch gets a row that cannot be
#                                                    written and is saved; the database must hold the item and the step anyway
#                                                    (row-by-row replay), the bad row lands in character-save-row-dropped.
# Run restart with SHUTDOWN_SAVE_SECONDS=0 in the environment (and the world recreated) to see the loss without the shutdown save.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
mode="${1:-restart}"; item="${2:-1451}"
sql() { docker compose exec -T db mysql -uroot -p"$(grep -E '^DB_ROOT_PASSWORD=' .env | cut -d= -f2)" -N nexus_forever_character -e "$1" </dev/null 2>/dev/null; }
out=$(scripts/gt.sh gt savetest "$item" "$([ "$mode" = fault ] && echo 1 || echo 0)" 2>&1 | grep savetest | tail -1)
echo "$out" | cut -c1-200
char=$(echo "$out" | grep -oE 'character=[0-9]+' | cut -d= -f2)
[ -n "$char" ] || { echo "no bot picked"; exit 1; }
count=$(echo "$out" | grep -oE 'count=[0-9]+' | cut -d= -f2)
quest=$(echo "$out" | grep -oE 'quest=[0-9]+' | cut -d= -f2)
index=$(echo "$out" | grep -oE 'index=[0-9]+' | cut -d= -f2)
progress=$(echo "$out" | grep -oE 'progress=[0-9]+->[0-9]+' | cut -d'>' -f2)
sleep 5
if [ "$mode" = restart ]; then
  t0=$(date +%s.%N); docker compose stop world >/dev/null 2>&1; t1=$(date +%s.%N)
  echo "stop took $(echo "$t1 - $t0" | bc) s"
fi
dbcount=$(sql "select coalesce(sum(stackCount),0) from item where ownerId=$char and itemId=$item and location=1")
dbprog=$(sql "select progress from character_quest_objective where id=$char and questId=$quest and \`index\`=$index")
echo "game: item $item x$count, quest $quest objective $index progress $progress"
echo "db:   item $item x${dbcount:-none}, quest $quest objective $index progress ${dbprog:-none}"
[ "$dbcount" = "$count" ] && [ "$dbprog" = "$progress" ] && echo "RESULT PASS" || echo "RESULT FAIL"
if [ "$mode" = restart ]; then
  docker compose start world >/dev/null 2>&1
  docker compose exec -T world sh -c 'tail -2 /cache/shutdown-save.log' </dev/null 2>/dev/null
else
  sleep 2; docker compose exec -T world sh -c 'cat /cache/failure-report.txt' </dev/null 2>/dev/null | grep -A3 -E '^== character-save-(failed|row-dropped) ' | cut -c1-200
  docker compose exec -T world sh -c 'grep -E "REPLAY|dropped" /cache/character-save.log | tail -3' </dev/null 2>/dev/null | cut -c1-200
fi
