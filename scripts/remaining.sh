#!/usr/bin/env bash
# Estimate how much is left to fix, from the last snapshot of a soak run (or the live server without an argument).
#
#   scripts/remaining.sh                  live server
#   scripts/remaining.sh soak/20260930-1100
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [ -n "${1:-}" ]; then
  last="$(ls -d "${1%/}"/snap-* | sort | tail -n1)"
  fail="$(cat "$last/failures.txt")"; eff="$(cat "$last/effects.txt")"
  cov="$(cat "${1%/}/quest-coverage-final.txt" 2>/dev/null || cat "${1%/}"/snap-001/quests.txt 2>/dev/null)"
else
  fail="$(scripts/report.sh failures)"; eff="$(scripts/gt.sh gt effects)"; cov="$(scripts/report.sh quests)"
fi

section() { awk -v c="$1" '$0 ~ "^== "c" "{f=1; next} /^== /{f=0} f' <<<"$fail"; }
sum()     { awk '{n=$1; sub(/x$/,"",n); s+=n} END{print s+0}'; }

src=Source/NexusForever.WorldServer
handled="$(grep -rhoE 'SpellEffectHandler\(SpellEffectType\.[A-Za-z0-9]+' $src | sort -u | wc -l)"
total="$(grep -cE '^\s+[A-Za-z0-9]+\s+= 0x' $src/Game/Spell/Static/SpellEffectType.cs)"
stored="$(section effect-stored | grep -oE 'x [A-Za-z0-9]+' | awk '{print $2}' | sort -u | wc -l)"
unh_kinds="$(grep -cE ': [0-9]+x' <<<"$eff")"
unh_total="$(grep -oE ': [0-9]+x' <<<"$eff" | tr -dc '0-9\n' | sum)"
pre_kinds="$(section unhandled-prerequisite-type | grep -cE '^ +[0-9]+x')"
pre_total="$(section unhandled-prerequisite-type | sum)"
cast_kinds="$(section cast-failed | grep -cE '^ +[0-9]+x')"
cast_total="$(section cast-failed | sum)"
exc="$(section map-update-exception | sum) map, $(section spell-update-exception | sum) spell"
syn="$(section objective-synthetic | sum)"

cat <<TXT
=== How much is left (approximate, from the counters of this run)

Spell effects
  effect types in the game:            $total
  with a handler in the server:        $handled   (of which $stored only store their data, see effect-stored)
  seen in play without any handler:    $unh_kinds types, $unh_total occurrences
Prerequisites
  types seen but not evaluated:        $pre_kinds types, $pre_total checks (spell casts pass them, quests do not)
Casting
  distinct failure reasons per spell:  $cast_kinds, $cast_total failed casts in total
Stability
  update exceptions:                   $exc
Quests
  synthetic credits given:             $syn (each is an objective that does not work for real yet)
$(grep -E 'Bots can|Not covered|synthetic|total' <<<"$cov" | head -12 | sed 's/^/  /')

Rough size of the whole job (my estimate, not measured)
  finish spell effects and prerequisites from data:   about 1-2 weeks of evenings   (this is what the data dumps feed)
  real quest objectives instead of synthetic:         about 2-3 weeks
  creatures casting spells + first dungeon:           about 3-4 weeks
  groups, companions, roles:                          about 2-3 weeks
  talking bots (rules, local model, retrieval):       about 2-3 weeks, can start any time
TXT
