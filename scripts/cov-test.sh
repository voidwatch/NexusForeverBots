#!/bin/bash
# 1 h coverage test: restart world, N bots, log distinct completed quests every 10 min to /tmp/cov.out
cd "$(dirname "$0")/.."
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
docker compose up -d >/dev/null 2>&1
sleep 90; scripts/bots.sh count ${1:-30} >/dev/null 2>&1
: > /tmp/cov.out
for i in 0 1 2 3 4 5 6; do
  echo "t+$((i*10))min $(docker compose exec -T world sh -c 'grep quest-coverage /cache/failure-report.txt')" >> /tmp/cov.out
  sleep 600
done
docker compose exec -T world sh -c 'grep -i -A6 "pick-filter\|bot-level-rotation" /cache/failure-report.txt | head -30' >> /tmp/cov.out
