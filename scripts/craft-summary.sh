#!/bin/bash
# Crafting counters from a failure report (default: the live world's /cache/failure-report.txt): crafts, catalysts, bonuses, additives,
# power cores, salvage, gathers, talents and the QA grants, one line per ledger group (total and kinds).
#   scripts/craft-summary.sh [failure-report file]
cd "$(dirname "$0")/.."
f="$1"
if [ -z "$f" ]; then f=/tmp/craft-summary-report.txt; docker compose exec -T world sh -c 'cat /cache/failure-report.txt' </dev/null > "$f" 2>/dev/null; fi
grep -E '^== ' "$f" | grep -E 'tradeskill|craft|catalyst|bonus-applied|additive|powercore|circuit|salvage|harvest|farm|talent|material' \
  | sed -E 's/^== ([^ ]+) \(([0-9]+) kinds, ([0-9]+) total\)/\3\t\1 (\2 kinds)/' | sort -k2 | head -60
echo "-- craft results"
grep -A12 '^== tradeskill-msg ' "$f" | grep -E 'craft' | head -8
