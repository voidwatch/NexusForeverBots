#!/bin/bash
# CPU percent (of one core) over 5 s and memory of the world container, through docker compose only (cgroup counters).
cd "$(dirname "$0")/.."
a="$(docker compose exec -T world sh -c 'grep usage_usec /sys/fs/cgroup/cpu.stat' </dev/null | awk '{print $2}')"; sleep 5
b="$(docker compose exec -T world sh -c 'grep usage_usec /sys/fs/cgroup/cpu.stat; cat /sys/fs/cgroup/memory.current' </dev/null | awk '{print $NF}' | tr '\n' ' ')"
echo "$a $b" | awk '{printf "cpu %.0f%%  mem %.2f GiB\n", ($2-$1)/50000, $3/1073741824}'
