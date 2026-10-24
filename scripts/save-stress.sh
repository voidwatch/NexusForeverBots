#!/usr/bin/env bash
# Character save stress test: online bots run random quest transitions with saves in between (gt queststress), the bot population
# is cut and refilled to force relogs, then every bot's quests are compared with its database rows (gt questverify).
# Needs the world running with bots. Prints the character save sections of the failure report and the verify summary.
#   scripts/save-stress.sh [minutes] [transitions per call] [1: also save after each call (overlapping saves), 0: 60 s timer only]
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
minutes="${1:-5}"; per="${2:-4}"; save="${3:-0}"
target=$(grep -E '^BOTS_TARGET=' .env | cut -d= -f2); target=${target:-30}
end=$(( $(date +%s) + minutes * 60 )); n=0
while [ "$(date +%s)" -lt "$end" ]; do
  scripts/gt.sh gt queststress "$per" "$save" >/dev/null 2>&1
  n=$((n + 1))
  # every 40 calls: a third of the bots log out and back in
  if [ $((n % 40)) = 0 ]; then
    scripts/gt.sh bot target $((target * 2 / 3)) >/dev/null 2>&1; sleep 2; scripts/gt.sh bot target "$target" >/dev/null 2>&1
  fi
  sleep 1
done
echo "stress calls: $n"; scripts/gt.sh gt queststress 0 0 2>&1 | tail -1 | cut -c1-300
sleep 20
scripts/gt.sh gt questverify >/dev/null 2>&1; sleep 40
docker compose exec -T world sh -c 'cat /cache/quest-verify.txt' </dev/null > /tmp/quest-verify.txt 2>&1
docker compose exec -T world sh -c 'cat /cache/failure-report.txt' </dev/null > /tmp/save-stress-fail.txt 2>&1
echo "verify: $(grep -c ' compared' /tmp/quest-verify.txt) bots, $(grep -v ' 0 differ' /tmp/quest-verify.txt | grep -c ' differ') with differences"
grep -A8 -E '^== (character-save-failed|character-save-cause|character-stale-load|character-quest-mismatch|character-save-resync|quest-stress-error) ' /tmp/save-stress-fail.txt | cut -c1-230 | head -60
