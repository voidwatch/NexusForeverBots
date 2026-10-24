#!/bin/bash
# CPU/memory bench for bisecting: the 10-minute gate split in foreground steps, with many CPU samples instead of one.
#   scripts/bench.sh start  <label> [bots] [KEY=VALUE...]  writes the pairs to .env (e.g. BOTS_DUNGEON=0; the docker wrapper drops shell env),
#                                              recreates the world with bots, 90 s
#   scripts/bench.sh sample <label> [n] [sec]  n cgroup samples (cpu usage_usec, memory.current) every sec seconds -> /tmp/bench-<label>.csv
#   scripts/bench.sh finish <label>            ledger exceptions, ticks, cpu avg/min/max, mem avg/max -> /tmp/bench-<label>.txt
cd "$(dirname "$0")/.."
cmd="$1"; label="$2"
csv="/tmp/bench-$label.csv"; out="/tmp/bench-$label.txt"
unset $(grep -E '^[A-Za-z_]+=' .env | cut -d= -f1) 2>/dev/null
case "$cmd" in
start)
    rm -f "$csv" "$out"
    for kv in "${@:4}"; do
        k="${kv%%=*}"
        if grep -qE "^$k=" .env; then sed -i "s|^$k=.*|$kv|" .env; else echo "$kv" >> .env; fi
    done
    scripts/bots.sh count "${3:-30}" >/dev/null 2>&1   # sets BOTS_TARGET and recreates the world with this shell's BOTS_* env
    docker compose up -d --force-recreate world >/dev/null 2>&1
    sleep 90
    date +%s > "/tmp/bench-$label.start"
    grep -E '^BOTS_(TARGET|DUNGEON|EVENTS|SYSTEM_BOTS)=' .env | tr '\n' ' ' > "/tmp/bench-$label.env"
    echo "started $label at $(date +%T): $(cat /tmp/bench-$label.env)"
    ;;
sample)
    n="${3:-10}"; sec="${4:-30}"
    for i in $(seq "$n"); do
        docker compose exec -T world sh -c 'grep usage_usec /sys/fs/cgroup/cpu.stat; cat /sys/fs/cgroup/memory.current' </dev/null 2>/dev/null \
            | awk -v t="$(( $(date +%s) - $(cat /tmp/bench-$label.start) ))" '{v[NR]=$NF} END{print t","v[1]","v[2]}' >> "$csv"
        sleep "$sec"
    done
    tail -1 "$csv"
    ;;
finish)
    docker compose exec -T world sh -c 'cat /cache/failure-report.txt' </dev/null > "/tmp/bench-$label-fail.txt" 2>&1
    scripts/gt.sh gt maps > "/tmp/bench-$label-maps.txt" 2>&1
    scripts/gt.sh gt profile show > "/tmp/bench-$label-profile.txt" 2>&1
    {
        echo "bench $label  $(git rev-parse --short HEAD)  $(date '+%F %T')"
        # warm-up excluded: samples after 4 min of bots
        echo "env: $(cat /tmp/bench-$label.env)"
        # warm-up excluded: from the first sample after 4 min of bots; cpu = cgroup usage over the window (100% = one core)
        awk -F, '$1>=240{if(t0==""){t0=$1;u0=$2} if(n&&$1>tp){c=($2-up)/(($1-tp)*10000); if(min==""||c<min)min=c; if(c>max)max=c} tp=$1;up=$2;n++;g=$3/1073741824;m+=g;if(g>mm)mm=g}
            END{printf "cpu avg %.0f%% (window %d s) interval min %.0f%% max %.0f%%  mem avg %.2f max %.2f GiB\n",(up-u0)/((tp-t0)*10000),tp-t0,min,max,m/n,mm}' "$csv"
        grep -E "^== (map-update-exception|spell-update-exception)" "/tmp/bench-$label-fail.txt" || echo "0 map/spell update exceptions"
        grep -o "quest-coverage: [0-9]* / [0-9]* doable" "/tmp/bench-$label-fail.txt"
        awk '/ticks/{for(i=1;i<=NF;i++) if($(i+1)=="ticks") {t=$i; if(t>mx)mx=t; if(mn==""||t<mn)mn=t}} END{print "ticks per world min " mn " max " mx}' "/tmp/bench-$label-maps.txt"
    } > "$out"
    cat "$out"
    ;;
esac
