#!/bin/bash
# system probe counts (ok / fail per kind) from a failure report: scripts/system-counts.sh [report-file] (default: the running world)
cd "$(dirname "$0")/.."
f="${1:-}"
if [ -n "$f" ]; then txt=$(cat "$f"); else txt=$(docker compose exec -T world sh -c 'cat /cache/failure-report.txt' </dev/null); fi
echo "$txt" | awk '/^== system-ok/{m="ok";next} /^== system-fail/{m="fail";next} /^==/{m=""} m!="" && /^  [0-9]+x /{split($0,a,"x "); n=a[1]+0; k=a[2]; sub(/ .*/,"",k); c[m" "k]+=n} END{for(x in c) print x, c[x]}' | sort
