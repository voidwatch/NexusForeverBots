#!/usr/bin/env bash
# Unattended bot run for collecting data over many hours.
#
#   setsid nohup scripts/soak.sh [HOURS] [BOTS] [MINUTES_BETWEEN_SNAPSHOTS] </dev/null >soak.log 2>&1 &
#   scripts/soak.sh 8 60 10        example: 8 hours, 60 immortal questing bots, snapshot every 10 minutes
# With BOTS_CLEAN=1 in .env the bots are clean bots and the run folder also gets quest-truth.tsv, quest-truth-open.tsv and
# quest-truth-summary.txt (scripts/quest-truth.sh; docs/TESTING.md "Clean-bot quest verification").
#
# At the end bundle.tar.gz holds everything in one file to attach. Everything lands in soak/<start time>/ : one folder per snapshot (failure report, quest coverage, map timings, unhandled effects,
# bot list, quest counts from the database), stats.csv (CPU and memory over time), events.log (restarts, throttling) and at the end
# errors.log (deduplicated warnings and errors). Read it in the morning with scripts/soak-summary.sh.
# If the server keeps using more than 70% of the machine the bot count is lowered by 10 (never below 20) so the run survives the night.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
[ -f .env ] || { echo "No .env here. Run ./install.sh first." >&2; exit 1; }

hours="${1:-8}"; bots="${2:-50}"; every="${3:-10}"
for v in "$hours" "$bots" "$every"; do case "$v" in ''|*[!0-9]*) echo "Numbers only." >&2; exit 1 ;; esac; done

start="$(date +%Y%m%d-%H%M)"
since_utc="$(date -u '+%F %T')"   # clean-bot completions from this time on belong to the run (quest-truth.tsv)
out="soak/$start"
mkdir -p "$out"
log() { echo "$(date '+%F %T') $*" | tee -a "$out/events.log"; }

set_env() {
  if grep -qE "^$1=" .env; then sed -i "s|^$1=.*|$1=$2|" .env; else printf '%s=%s\n' "$1" "$2" >> .env; fi
}
get_env() { grep -E "^$1=" .env | tail -n1 | cut -d= -f2- || true; }

# exported shell variables would override .env for docker compose
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null || true

set_env BOTS_TARGET "$bots"; set_env COMBAT 1; set_env COMBAT_AGGRO 0
set_env PROFILE 1; set_env BOTS_IMMORTAL 1; set_env LOG_LEVEL Info; set_env TICK_MS 20

log "Soak run: $hours h, $bots bots, snapshot every $every min, output in $out"
docker compose up -d >/dev/null 2>&1 || log "docker compose up failed"

cores="$(nproc 2>/dev/null || echo 4)"
limit=$(( cores * 70 ))
over=0
end=$(( $(date +%s) + hours * 3600 ))
n=0

db() {
  local pw; pw="$(get_env DB_ROOT_PASSWORD)"
  docker compose exec -T db mysql -uroot -p"$pw" -e "$1" </dev/null 2>&1
}

snapshot() {
  n=$((n + 1))
  local dir; dir="$out/snap-$(printf '%03d' "$n")"
  mkdir -p "$dir"
  date '+%F %T' > "$dir/time.txt"
  scripts/report.sh failures > "$dir/failures.txt" 2>&1 || true
  [ "$n" -le 2 ] && scripts/report.sh quests > "$dir/quests.txt" 2>&1
  scripts/gt.sh gt maps    > "$dir/maps.txt"    2>&1 || true
  scripts/gt.sh gt profile show > "$dir/profile.txt" 2>&1 || true
  scripts/gt.sh gt memory collect > "$dir/memory.txt" 2>&1 || true
  scripts/gt.sh gt effects > "$dir/effects.txt" 2>&1 || true
  scripts/gt.sh bot list   > "$dir/bots.txt"    2>&1 || true
  # clean-bot runs (BOTS_CLEAN=1): quest truth so far, every 6th snapshot (the open list asks every clean bot about every open quest)
  [ "$(get_env BOTS_CLEAN)" = 1 ] && [ $(( n % 6 )) -eq 0 ] && scripts/quest-truth.sh "$dir" "$since_utc" >/dev/null 2>&1
  db "select cq.state, count(*) as rows_, count(distinct cq.questId) as quests from nexus_forever_character.character_quest cq join nexus_forever_character.\`character\` c on c.id=cq.id join nexus_forever_auth.account a on a.id=c.accountId where a.email like 'bot.%' group by cq.state;" > "$dir/quests-db.txt" || true

  local cpu mem restarts
  # through docker compose only (plain docker commands can be blocked): read the container's cgroup counters two seconds apart
  local cg1 cg2 mem_bytes
  cg1="$(docker compose exec -T world sh -c 'grep usage_usec /sys/fs/cgroup/cpu.stat; cat /sys/fs/cgroup/memory.current' </dev/null 2>/dev/null | tr '\n' ' ')"
  sleep 2
  cg2="$(docker compose exec -T world sh -c 'grep usage_usec /sys/fs/cgroup/cpu.stat; cat /sys/fs/cgroup/memory.current' </dev/null 2>/dev/null | tr '\n' ' ')"
  cpu="$(echo "$cg1 $cg2" | awk '{ if (NF >= 6) printf "%.2f", ($5 - $2) / 2000000 * 100 }')"
  mem_bytes="$(echo "$cg2" | awk '{print $3}')"
  mem="$(echo "${mem_bytes:-0}" | awk '{ if ($1 > 0) printf "%.3fGiB", $1 / 1073741824 }')"
  restarts="$(docker compose ps world --format '{{.Status}}' 2>/dev/null | head -1 | tr ',' ';' || echo '?')"
  [ -f "$out/stats.csv" ] || echo "time,cpu_percent,memory,restarts,bots_target" > "$out/stats.csv"
  echo "$(date '+%F %T'),${cpu:-?},${mem:-?},$restarts,$(get_env BOTS_TARGET)" >> "$out/stats.csv"
  # managed heap next to the container memory: read from the failure report, which the server writes from a timer thread, so it is
  # there even while the world thread hangs
  local heap
  heap="$(docker compose exec -T world sh -c 'grep -A1 "^== memory" /cache/failure-report.txt' </dev/null 2>/dev/null | tail -1 \
    | sed -E 's/^ *heap ([0-9]+) MB, gc heap [0-9]+ MB, fragmented [0-9]+ MB, committed ([0-9]+) MB, limit ([0-9]+) MB, gen0\/1\/2 [0-9]+\/[0-9]+\/([0-9]+),.* ([0-9]+) queued grid actions.*/heap \1MB committed \2MB limit \3MB gen2 \4 queued \5/')"
  log "snapshot $n: cpu ${cpu:-?}% mem ${mem:-?} status $restarts bots $(get_env BOTS_TARGET) ${heap:-heap ?}"

  # keep the server alive and the load bearable
  if [ -z "$(docker compose ps --status running -q world 2>/dev/null)" ]; then
    log "world container is not running, starting it"
    docker compose up -d world >/dev/null 2>&1 || true
  fi

  local cpu_int="${cpu%%.*}"
  if [[ "$cpu_int" =~ ^[0-9]+$ ]] && [ "$cpu_int" -gt "$limit" ]; then
    over=$((over + 1))
  else
    over=0
  fi
  if [ "$over" -ge 2 ]; then
    local current; current="$(get_env BOTS_TARGET)"
    if [ "$current" -gt 20 ]; then
      local lower=$(( current - 10 ))
      [ "$lower" -lt 20 ] && lower=20
      log "cpu above ${limit}% twice in a row, lowering bots from $current to $lower"
      set_env BOTS_TARGET "$lower"
      docker compose up -d world >/dev/null 2>&1 || true
    fi
    over=0
  fi
}

# give the world time to load tables and maps and the bots time to log in
sleep 240
while [ "$(date +%s)" -lt "$end" ]; do
  snapshot
  # full spell/prerequisite data dump once things have warmed up, and again every 2 hours while the counters keep growing
  if [ "$n" -eq 3 ] || [ $(( n % 12 )) -eq 0 ]; then
    log "spell data dump at snapshot $n"
    scripts/spell-data.sh "$out/spell-data-snap-$(printf '%03d' "$n").txt" >/dev/null 2>&1 || true
  fi
  sleep $(( every * 60 ))
done

snapshot
log "final data dumps"
scripts/spell-data.sh "$out/spell-data-final.txt" >/dev/null 2>&1 || true
scripts/report.sh quests > "$out/quest-coverage-final.txt" 2>&1 || true
scripts/remaining.sh "$out" > "$out/remaining.txt" 2>&1 || true
[ "$(get_env BOTS_CLEAN)" = 1 ] && { log "clean-bot quest truth"; scripts/quest-truth.sh "$out" "$since_utc" >/dev/null 2>&1 || true; }
log "collecting warnings and errors"
docker compose logs world --no-color 2>&1 | grep -E "\[(WARN|ERROR|FATAL)\]|Exception" \
  | sed -E 's/^[^|]*\| //; s/[0-9]+ms/Nms/g' | sort | uniq -c | sort -rn | head -200 > "$out/errors.log"
docker compose logs world --no-color --tail 60000 2>&1 | sed -E 's/^[^|]*\| //' | gzip > "$out/world-log-tail.txt.gz" || true
scripts/soak-summary.sh "$out" > "$out/summary.txt" 2>&1 || true
cp .env "$out/env.txt" 2>/dev/null && sed -i -E 's/(PASSWORD|SECRET|TOKEN)[A-Za-z_]*=.*/\1=hidden/' "$out/env.txt"
tar -czf "$out/bundle.tar.gz" -C soak "$start" --exclude="$start/bundle.tar.gz" 2>/dev/null || true
log "done. Attach $out/bundle.tar.gz (everything: summary, remaining.txt, spell data, snapshots, logs). Bots keep running; stop them with: scripts/bots.sh disable"
