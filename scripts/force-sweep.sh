#!/bin/bash
# Forced quest sweep: free quest bots take every open doable quest (not waiting for an open prerequisite quest) through the forced
# path, one quest per bot, bounded in time; after a completion the same bot walks the quest's follow-ups (server: Bots/ForceSweep.cs).
#
#   scripts/force-sweep.sh start [bots=60] [minutes per quest=8] [queue file]   restart world with quest bots only, start the sweep
#   scripts/force-sweep.sh wait [minutes<=8]                                     sleep in the foreground, then print status
#   scripts/force-sweep.sh status                                                queue, running, outcome counts
#   scripts/force-sweep.sh collect                                               docs/SWEEP-RESULTS.tsv + grouped summary (<= 30 lines)
#   scripts/force-sweep.sh stop                                                  stop, restore .env
# A queue file (one quest id per line) limits the sweep to those quests, e.g. a re-run on what is left; a line "+" adds the default list, "*" also the chain-blocked quests (prerequisite quests skipped, via prereq-bypass).
cd "$(dirname "$0")/.."
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
cmd="${1:-status}"; shift

status() { scripts/gt.sh bot sweep status 2>&1 | cut -c1-300; }

case "$cmd" in
  start)
    bots="${1:-60}"; mins="${2:-8}"; qfile="${3:-}"
    [ -f /tmp/env.force-sweep.bak ] || cp .env /tmp/env.force-sweep.bak
    sed -i 's/^BOTS_SYSTEM_BOTS=.*/BOTS_SYSTEM_BOTS=0/; s/^BOTS_EVENT_BOTS=.*/BOTS_EVENT_BOTS=0/; s/^BOTS_DUNGEON=.*/BOTS_DUNGEON=0/; s/^BOTS_EVENTS=.*/BOTS_EVENTS=0/' .env
    scripts/bots.sh count "$bots" >/dev/null 2>&1
    docker compose up -d >/dev/null 2>&1
    if [ -n "$qfile" ]; then
      docker compose exec -T world sh -c 'cat > /cache/sweep-queue.txt' < "$qfile"
    else
      docker compose exec -T world rm -f /cache/sweep-queue.txt </dev/null
    fi
    # bots log in over a minute or two; the spawn index loads in the background
    for _ in $(seq 1 30); do
      sleep 10
      n=$(scripts/gt.sh bot list 2>/dev/null | grep -oE 'running [0-9]+' | grep -oE '[0-9]+')
      cov=$(docker compose exec -T world sh -c 'grep -c "^quest-coverage: [0-9]" /cache/failure-report.txt' </dev/null 2>/dev/null)
      [ "${n:-0}" -ge "$bots" ] && [ "${cov:-0}" -ge 1 ] && break
    done
    sleep 30
    docker compose exec -T world sh -c 'grep "^quest-coverage" /cache/failure-report.txt' </dev/null | cut -c1-200
    scripts/gt.sh bot sweep start "$mins" 2>&1 | cut -c1-300
    ;;
  wait)
    m="${1:-8}"; [ "$m" -gt 8 ] && m=8
    sleep $((m*60)); status ;;
  status) status ;;
  stop)
    scripts/gt.sh bot sweep stop 2>&1 | cut -c1-300
    [ -f /tmp/env.force-sweep.bak ] && cp /tmp/env.force-sweep.bak .env && rm -f /tmp/env.force-sweep.bak
    ;;
  collect)
    out="${1:-docs/SWEEP-RESULTS.tsv}"
    docker compose exec -T world cat /cache/sweep-results.tsv </dev/null > "$out"
    echo "$(($(wc -l < "$out") - 1)) quests in $out"
    awk -F'\t' 'NR>1{print $2" via "$3}' "$out" | sort | uniq -c | sort -rn
    echo "-- normal picker verdict of completed quests:"
    awk -F'\t' 'NR>1 && $2=="completed"{print $7}' "$out" | sort | uniq -c | sort -rn | head -6
    echo "-- stall/timeout groups (open objective type, trace):"
    awk -F'\t' 'NR>1 && ($2=="stalled" || $2=="timeout"){
        t=""; n=split($8,o," "); for(i=1;i<=n;i++) if (o[i] ~ /\*$/) { split(o[i],p,":"); t=t p[2] "," }
        d=$9; sub(/.*last trace: /,"",d); gsub(/[0-9]+/,"#",d); print "type " t " | " substr(d,1,70) }' "$out" | sort | uniq -c | sort -rn | head -12
    echo "-- not-takeable:"
    awk -F'\t' 'NR>1 && $2=="not-takeable"{d=$9; gsub(/[0-9]+/,"#",d); print substr(d,1,90)}' "$out" | sort | uniq -c | sort -rn | head -5
    ;;
esac
