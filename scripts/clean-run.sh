#!/usr/bin/env bash
# Clean-bot verification run in phases (docs/TESTING.md "Clean-bot quest verification"):
#   scripts/clean-run.sh start [bots] [synthetic 0|1]   sets BOTS_CLEAN=1, BOTS_TARGET, BOTS_SYNTHETIC in .env, stores the database counts of every
#                                                       other account/character, starts the stack; the run folder is soak/clean-<time>/
#   scripts/clean-run.sh restart                        restarts the world container in the middle of the run (clean bots must come back)
#   scripts/clean-run.sh collect                        quest truth, failure report, map ticks, CPU, database counts after + diff, verdict
# The run folder of the last start is in /tmp/clean-run-dir.txt. Nothing is deleted; other accounts are never logged in.
cd "$(dirname "$0")/.."
set_env() { if grep -qE "^$1=" .env; then sed -i "s|^$1=.*|$1=$2|" .env; else printf '%s=%s\n' "$1" "$2" >> .env; fi; }
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
case "${1:-}" in
  start)
    bots="${2:-30}"; synth="${3:-0}"
    dir="soak/clean-$(date +%Y%m%d-%H%M)"; mkdir -p "$dir"; echo "$dir" > /tmp/clean-run-dir.txt
    set_env BOTS_CLEAN 1; set_env BOTS_TARGET "$bots"; set_env BOTS_SYNTHETIC "$synth"; set_env COMBAT 1
    # stop the world first: its logout saves of the bots still online would land after the count (2026-10-19 drift, docs/ASSUMPTIONS.md)
    docker compose stop world >/dev/null 2>&1
    scripts/db-counts.sh > "$dir/db-before.txt"
    date -u '+%F %T' > "$dir/since.txt"
    grep -E '^(BOTS_|COMBAT|TICK_MS)' .env > "$dir/env.txt"
    docker compose up -d >/dev/null 2>&1
    echo "started $dir: $bots clean bots, synthetic $synth, since $(cat "$dir/since.txt") UTC"
    ;;
  restart)
    dir="$(cat /tmp/clean-run-dir.txt)"
    scripts/clean-bots.sh 200 | awk 'NR>1 && $2!="NULL" {n++; c+=$5} END {print "before restart: " n " clean characters, " c " completed quest rows"}' | tee -a "$dir/restart.txt"
    docker compose up -d --force-recreate world >/dev/null 2>&1   # a restart that also deploys a newer image
    echo "restarted at $(date -u '+%F %T') UTC" | tee -a "$dir/restart.txt"
    ;;
  collect)
    dir="$(cat /tmp/clean-run-dir.txt)"
    scripts/quest-truth.sh "$dir" "$(cat "$dir/since.txt")" > /dev/null
    docker compose exec -T world sh -c 'cat /cache/failure-report.txt' </dev/null > "$dir/failures.txt" 2>&1
    scripts/gt.sh gt maps > "$dir/maps.txt" 2>&1
    scripts/cpu.sh > "$dir/cpu.txt" 2>&1
    scripts/gt.sh bot list > "$dir/bots.txt" 2>&1
    scripts/clean-bots.sh 200 > "$dir/clean-bots.txt"
    scripts/db-counts.sh > "$dir/db-after.txt"
    ex=$(grep -cE '^== (map-update-exception|spell-update-exception)' "$dir/failures.txt")
    cs=$(grep -E '^== character-save-failed ' "$dir/failures.txt" | grep -oE '[0-9]+ total' | grep -oE '[0-9]+'); cs=${cs:-0}
    dbd=$(diff <(grep -v '^#' "$dir/db-before.txt") <(grep -v '^#' "$dir/db-after.txt") | grep -c '^[<>]')
    { [ "$ex" = 0 ] && echo "exceptions PASS" || echo "exceptions FAIL ($ex sections)"
      [ "$cs" = 0 ] && echo "character-saves PASS" || echo "character-saves FAIL ($cs)"
      [ "$dbd" = 0 ] && echo "other accounts/characters unchanged PASS ($(grep -c . "$dir/db-before.txt") lines compared)" || echo "other accounts/characters FAIL ($dbd lines differ)"
      grep '^# ' "$dir/db-after.txt" | paste -sd';'
      grep -E '^world (22|51):' "$dir/maps.txt" | paste -sd';'; grep -iE 'cpu|mem' "$dir/cpu.txt" | head -2 | paste -sd';'
    } > "$dir/verdict.txt"
    cat "$dir/verdict.txt"; head -12 "$dir/quest-truth-summary.txt"
    ;;
  *) sed -n 2,8p "$0"; exit 1 ;;
esac
