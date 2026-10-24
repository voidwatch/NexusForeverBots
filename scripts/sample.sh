#!/bin/bash
# One line per call for soaks without docker stats: wall time, world process CPU seconds (utime+stime), RSS MB, bots online,
# min ticks over maps, quest-coverage numbers. Append to a file and diff two lines for CPU %: (cpu2-cpu1)/(t2-t1).
#   scripts/sample.sh >> /tmp/soak-samples.txt
cd "$(dirname "$0")/.."
read -r cpu rss < <(docker compose exec -T world sh -c 'hz=$(getconf CLK_TCK); set -- $(cut -d" " -f14,15 /proc/1/stat); echo $(( ($1+$2)/hz )) $(( $(grep VmRSS /proc/1/status | tr -s " " | cut -d" " -f2) / 1024 ))' 2>/dev/null)
cov=$(docker compose exec -T world sh -c 'head -3 /cache/failure-report.txt' 2>/dev/null | grep -oE '[0-9]+ / [0-9]+ doable quests completed at least once, [0-9]+ without' | tr -d ',')
ticks=$(scripts/gt.sh gt maps 2>/dev/null | awk '$2=="51:"{print $(NF-1)}')
bots=$(scripts/gt.sh bot list 2>/dev/null | grep -oE 'running [0-9]+' | head -1)
echo "$(date +%s) $(date +%H:%M) cpu_s=$cpu rss_mb=$rss $bots ticks51=$ticks | $cov"
