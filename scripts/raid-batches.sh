#!/usr/bin/env bash
# Raid worlds in batches of three (each world's terrain costs 1-2 GB heap; all eight at once hit the 14g limit).
#   setsid nohup scripts/raid-batches.sh OUTDIR [minutes per batch] </dev/null >/tmp/raid-batches.log 2>&1 &
cd "$(dirname "$0")/.."
out="$1"; min="${2:-25}"
for w in "1333 1462 3032" "3040 3041 3102" "3524 3042"; do
  scripts/instance-run.sh start "$w" 8
  sleep $(( min * 60 ))
  scripts/instance-run.sh collect "$out"
done
scripts/instance-run.sh restore
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
docker compose up -d world
echo finished $(date +%H:%M)
