#!/usr/bin/env bash
# Summarise a soak run (scripts/soak.sh). Without an argument the newest run in soak/ is used.
#
#   scripts/soak-summary.sh
#   scripts/soak-summary.sh soak/20260929-2245
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

dir="${1:-$(ls -d soak/*/ 2>/dev/null | sort | tail -n1)}"
dir="${dir%/}"
[ -d "$dir" ] || { echo "No soak run found. Start one with scripts/soak.sh." >&2; exit 1; }

echo "=== Soak run $dir"
echo
echo "--- Events"
cat "$dir/events.log" 2>/dev/null | tail -n 25
echo
echo "--- Load (CPU % and memory over the run)"
if [ -f "$dir/stats.csv" ]; then
  awk -F, 'NR>1 && $2 ~ /^[0-9.]+$/ {v=$2+0; n++; s+=v; if(n==1||v>m)m=v; if(n==1||v<min)min=v} END {if(n) printf "snapshots %d, cpu min %.0f%% avg %.0f%% max %.0f%%\n", n, min, s/n, m}' "$dir/stats.csv"
  tail -n 3 "$dir/stats.csv"
fi
echo
last="$(ls -d "$dir"/snap-* 2>/dev/null | sort | tail -n1)"
first="$(ls -d "$dir"/snap-* 2>/dev/null | sort | head -n1)"
if [ -n "$last" ]; then
  echo "--- Quests in the database, first snapshot vs last (state, rows, distinct quests)"
  echo "first:"; cat "$first/quests-db.txt" 2>/dev/null
  echo "last:";  cat "$last/quests-db.txt" 2>/dev/null
  echo
  echo "--- Quest coverage"
  head -n 3 "$dir"/snap-001/quests.txt 2>/dev/null
  echo
  if [ -f "$dir/quest-truth-summary.txt" ]; then
    echo "--- Clean-bot quest truth (quest-truth.tsv, quest-truth-open.tsv; docs/TESTING.md)"
    cat "$dir/quest-truth-summary.txt"
    echo
  fi
  echo "--- Latest failure report (top of each group)"
  awk '/^== /{c=0} {c++; if (c<=8) print}' "$last/failures.txt" 2>/dev/null
  echo
  echo "--- Map update timings (last snapshot)"
  cat "$last/maps.txt" 2>/dev/null
  echo
  echo "--- Unhandled spell effects (last snapshot)"
  head -n 12 "$last/effects.txt" 2>/dev/null
fi
echo
echo "--- Most frequent warnings and errors"
head -n 25 "$dir/errors.log" 2>/dev/null || echo "(errors.log is written when the run ends)"
