#!/usr/bin/env bash
# Dump the game table data behind everything the server could not handle in spells, so handlers can be written from real data.
# Uses the counters of the running server (run it after some bots have been active), prints to the terminal and to spell-data.txt.
#
#   scripts/spell-data.sh [output file]
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

out="${1:-spell-data.txt}"
{
  echo "== Unhandled spell effects and one example spell each"
  scripts/gt.sh gt effects | while read -r line; do
    echo "$line"
    id="$(sed -n 's/.*example spell \([0-9]*\).*/\1/p' <<<"$line")"
    [ -n "$id" ] && scripts/gt.sh gt spell "$id"
  done
  echo
  echo "== What the data fields of effect types mean (grouped, with example spell names)"
  for spec in "VitalModifier DataBits00" "Proc DataBits00" "Proc DataBits03" "UnitPropertyModifier DataBits00" "UnitPropertyModifier DataBits01" "ModifySpellCooldown DataBits00" "ForcedMove DataBits00" "CCStateSet DataBits00" "SpellEffectImmunity DataBits00"; do
    scripts/gt.sh gt effectdata $spec
  done
  echo
  echo "== Decoded examples (raw and float, with the spell each proc casts)"
  scripts/gt.sh gt effectdump UnitPropertyModifier DataBits01 DataBits00 24
  scripts/gt.sh gt effectdump Proc DataBits00 DataBits03 30
  scripts/gt.sh gt effectdump VitalModifier DataBits00 DataBits09 20
  scripts/gt.sh gt effectdump ModifySpellCooldown DataBits00 DataBits02 12
  scripts/gt.sh gt effectdump PersonalDmgHealMod DataBits00 DataBits01 8
  scripts/gt.sh gt effectdump Stealth DataBits02 DataBits00 6
  scripts/gt.sh gt effectdump ForcedMove DataBits00 DataBits06 12
  scripts/gt.sh gt effectdump SpellEffectImmunity DataBits00 DataBits01 12
  echo
  echo "== Every unhandled or stored effect type, grouped by its first two data fields"
  { scripts/gt.sh gt effects | sed -E 's/^([A-Za-z0-9]+):.*/\1/'
    scripts/report.sh failures | sed -n '/^== effect-stored/,/^== /p' | sed -nE 's/^ +[0-9]+x ([A-Za-z0-9]+) .*/\1/p'
  } | grep -E '^[A-Za-z0-9]+$' | sort -u | while read -r name; do
    scripts/gt.sh gt effectdump "$name" DataBits00 DataBits01 8
  done
  echo
  echo "== Unhandled prerequisite types"
  scripts/report.sh failures | sed -n '/^== unhandled-prerequisite-type/,/^== /p' | grep -E '^ +[0-9]+x ' | sed -E 's/^ +[0-9]+x ([0-9]+) .*/\1/' | sort -un | while read -r type; do
    scripts/gt.sh gt prereq "$type"
  done
  echo
  echo "== Prerequisite rows behind the unhandled types (where each is used)"
  scripts/report.sh failures | sed -n '/^== unhandled-prerequisite-type/,/^== /p' | sed -nE 's/^ +[0-9]+x [0-9]+ row ([0-9]+) .*/\1/p' | sort -un | head -40 | while read -r row; do
    scripts/gt.sh gt prereqrow "$row"
  done
  echo
  echo "== Where the object ids of types 118, 129, 68 and 43 show up in the tables"
  scripts/report.sh failures | sed -n '/^== unhandled-prerequisite-type/,/^== /p' | sed -nE 's/^ +[0-9]+x (118|129|68|43) row .*object ([0-9]+)\].*/\1 \2/p' | sort -u | head -24 | while read -r type obj; do
    echo "type $type object $obj:"
    scripts/gt.sh gt findall "$obj" | head -14
  done
  echo
  echo "== Quest objectives behind kills that matched nothing and stalled quests"
  scripts/report.sh failures | sed -n '/^== quest-kill-not-a-target/,/^== /p;/^== quest-stalled/,/^== /p' | grep -oE 'objective [0-9]+' | awk '{print $2}' | sort -un | head -40 | while read -r obj; do
    scripts/gt.sh gt objective "$obj"
  done
  scripts/report.sh failures | sed -n '/^== quest-kill-not-a-target/,/^== /p' | grep -oE '\[creature [0-9]+\]' | grep -oE '[0-9]+' | sort -un | head -20 | while read -r c; do
    scripts/gt.sh gt creature "$c"
  done
  echo
  echo "== Spell with the missing telegraph"
  scripts/gt.sh gt spell 34426
  scripts/gt.sh gt effectdump Proc DataBits03 DataBits00 40
  echo
  echo "== Cast failures and stored effects as the server saw them"
  scripts/report.sh failures | sed -n '/^== cast-failed/,/^== /p;/^== effect-stored/,/^== /p;/^== stealth/,/^== /p;/^== cc-state/,/^== /p'
  echo
  echo "== Spells that fail most often"
  scripts/report.sh failures | sed -n '/^== cast-failed/,/^== /p' | grep -E 'spell [0-9]+' | sed -E 's/.*spell ([0-9]+).*/\1/' | sort -un | head -8 | while read -r id; do
    scripts/gt.sh gt spell "$id"
  done
} 2>&1 | tee "$out"
echo
echo "Saved to $out"
