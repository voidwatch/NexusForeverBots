#!/usr/bin/env bash
# What bots have done with each quest, kept across restarts (quest-outcomes.txt in the world cache volume).
#
#   scripts/outcomes.sh          totals, and the quests that were taken but never completed, most stalled first
#   scripts/outcomes.sh raw      the whole file: quest, accepted, completed, stalled, last stall
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

file="$(docker compose exec -T world sh -c 'cat /cache/quest-outcomes.txt 2>/dev/null' </dev/null)"
[ -n "$file" ] || { echo "No outcomes yet, run bots for a while."; exit 0; }

if [ "${1:-}" = raw ]; then echo "$file"; exit 0; fi

echo "$file" | awk -F'\t' '
  { n++; if ($3 > 0) { done++; if ($3 > $6) normal++; if ($6 > 0) forced++ } else { tried++; stuck[$1] = $4 "\t" $5 } }
  END {
    printf "quests taken by bots: %d, completed at least once: %d (by the normal picker at least once: %d, forced at least once: %d), taken but never completed: %d\n", n, done, normal, forced, tried
    print "never completed (stalls, last stall):"
    for (q in stuck) print "  quest " q "\t" stuck[q]
  }' | sort -t$'\t' -k2 -nr | head -60
