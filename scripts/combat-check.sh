#!/usr/bin/env bash
# Compare the combat numbers the world server computes ('gt combat') with the numbers expected from the tables and the sources
# (scripts/research/combat_expect.py, see docs/ASSUMPTIONS.md "Combat formulas"). Needs the world running. Prints one line per check.
#   scripts/combat-check.sh
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
exp=$(cd scripts/research && python3 combat_expect.py) || { echo "expected values failed"; exit 1; }
got=$(mktemp)
# gt combat <level> <spell4|0> <creature|0> <power|0>: the console parser needs every argument
# level 50 row assault rating 3522.83 + 4176 (ilvl 50 weapon), the same "%g" value combat_expect.py uses as the key
gear_power=7698.83
for q in "1 43498 0 0" "10 43498 33504 0" "30 43498 0 0" "50 43498 59617 0" "50 41457 22169 17765" "50 49679 0 0" "50 6962 0 0" "50 43498 0 $gear_power"; do
  set -- $q
  # with a power override only the spell values belong to that query (player.* would show the overridden row)
  scripts/gt.sh gt combat $q 2>&1 | awk -v l="$1" -v p="$4" '$1=="value"{k=$2; if (k ~ /^spell\./) k="L" l (p != "0" ? "P" p : "") "." k; else if (p != "0" && k ~ /^player\./) next; print k, $3}' >> "$got"
done
# equipped item stats: server formula (gt itemstats) against archived Jabbithole tooltips (scripts/research/item_expect.py check)
items=$(cd scripts/research && python3 item_expect.py check) || { echo "item expectations failed"; exit 1; }
exp="$exp
$(cut -d' ' -f1-3,4- <<< "$items")"
ids=$(awk '{split($1,a,"."); print a[2]}' <<< "$items" | sort -u | paste -sd, -)
scripts/gt.sh gt itemstats "$ids" 2>&1 | awk '$1=="value"{print $2, $3}' >> "$got"
pass=0; fail=0; missing=0
while read -r key want tol src; do
  have=$(awk -v k="$key" '$1==k{print $2; exit}' "$got")
  if [ -z "$have" ]; then missing=$((missing+1)); printf 'MISSING %-40s expected %s  (%s)\n' "$key" "$want" "$src"; continue; fi
  if awk -v a="$have" -v b="$want" -v t="$tol" 'BEGIN{d=a-b; if(d<0)d=-d; m=(b<0?-b:b); exit !(d<=t*(m>1?m:1))}'; then
    pass=$((pass+1)); printf 'ok      %-40s %12s\n' "$key" "$have"
  else
    fail=$((fail+1)); printf 'DIFF    %-40s server %s expected %s  (%s)\n' "$key" "$have" "$want" "$src"
  fi
done <<< "$exp"
rm -f "$got"
echo "combat-check: $pass ok, $fail differ, $missing missing"
[ "$fail" -eq 0 ] && [ "$missing" -eq 0 ]
