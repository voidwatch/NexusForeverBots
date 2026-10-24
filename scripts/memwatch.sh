#!/usr/bin/env bash
# Prints container memory and the managed heap (gt memory, with and without a forced collection) every N seconds, to tell a leak from a lazy GC.
#   scripts/memwatch.sh [SECONDS_BETWEEN] [ROUNDS]
cd "$(dirname "$0")/.."
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
every="${1:-300}"; rounds="${2:-12}"
for i in $(seq 1 "$rounds"); do
  echo "== $(date +%T) container $(docker compose exec -T world sh -c 'cat /sys/fs/cgroup/memory.current' | awk '{printf "%.2f GB", $1/1073741824}')"
  scripts/gt.sh gt memory show 2>&1 | head -3
  scripts/gt.sh gt memory collect 2>&1 | head -3
  sleep "$every"
done
