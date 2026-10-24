#!/usr/bin/env bash
# Clean-bot quest truth for one run (NF_BOTS_CLEAN=1, docs/TESTING.md "Clean-bot quest verification").
#   scripts/quest-truth.sh <out-dir> [since "YYYY-MM-DD HH:MM:SS" UTC]
# Writes into <out-dir>: quest-truth.tsv (completions by clean bots since the time, all of them without it), quest-truth-open.tsv (doable
# quests no clean bot has completed yet, with the reason; needs the world running), quest-truth-summary.txt (the table), and prints the table.
cd "$(dirname "$0")/.."
out="${1:?out dir}"; since="${2:-}"
mkdir -p "$out"
scripts/gt.sh gt questtruth > "$out/quest-truth-gt.txt" 2>&1 || true
docker compose exec -T world sh -c 'cat /cache/truth/quest-truth.tsv 2>/dev/null' </dev/null | awk -F'\t' -v s="$since" 'NR==1 || s=="" || $1 >= s' > "$out/quest-truth.tsv"
docker compose exec -T world sh -c 'cat /cache/truth/quest-truth-open.tsv 2>/dev/null' </dev/null > "$out/quest-truth-open.tsv"
python3 scripts/quest-truth.py "$out" > "$out/quest-truth-summary.txt" 2>&1
head -1 "$out/quest-truth-gt.txt" >> "$out/quest-truth-summary.txt"
head -40 "$out/quest-truth-summary.txt"
