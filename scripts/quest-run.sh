#!/bin/bash
# Quest throughput run: only quest bots (no system probers, no event bots) for N minutes, then a digest.
#   setsid nohup scripts/quest-run.sh 20 30 </dev/null >quest-run.log 2>&1 &     (minutes, bots)
# Result in quest-run.out: coverage line, completions per bot-hour, why quests are still open.
cd "$(dirname "$0")/.."
mins="${1:-20}"; bots="${2:-30}"
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
cp .env /tmp/env.quest-run.bak
sed -i 's/^BOTS_SYSTEM_BOTS=.*/BOTS_SYSTEM_BOTS=0/; s/^BOTS_EVENT_BOTS=.*/BOTS_EVENT_BOTS=0/' .env
docker compose up -d >/dev/null 2>&1
sleep 120
scripts/bots.sh count "$bots" >/dev/null 2>&1
start=$(date +%s)
sleep $((mins*60))
{
  echo "== quest run $mins min, $bots bots"
  docker compose exec -T world sh -c 'grep -E "^quest-coverage" /cache/failure-report.txt; grep -E "completed quest|accepted quest" /cache/failure-report.txt | wc -l; grep -A14 "== bot-quest-progress" /cache/failure-report.txt | head -3; grep -A12 "== quest-stalled" /cache/failure-report.txt | head -8; grep -A6 "== bot-level-rotation" /cache/failure-report.txt | head -3' </dev/null | cut -c1-200
  docker compose exec -T world sh -c 'printf "qrun1\tgt questopen\n" >> /cache/commands.txt' </dev/null
  sleep 20
  docker compose exec -T world sh -c 'cat /cache/out/qrun1.txt' </dev/null | cut -c1-240
} > quest-run.out 2>&1
cp /tmp/env.quest-run.bak .env
