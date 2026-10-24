#!/bin/bash
# Live stream of what bots do: time, bot, level, zone, event. Written by the world server to /cache/bot-feed.log (20 MB, rotated).
#   scripts/bot-feed.sh [-f] [bot-name] [last N lines]     (at most 40 lines when not following)
cd "$(dirname "$0")/.."
follow=0; [ "$1" = "-f" ] && { follow=1; shift; }
name="${1:-}"; n="${2:-40}"; [ "$n" -gt 40 ] && [ $follow = 0 ] && n=40
filter() { if [ -n "$name" ]; then grep -F -- "	$name	"; else cat; fi; }
if [ $follow = 1 ]; then
  docker compose exec -T world tail -n 0 -F /cache/bot-feed.log | filter
else
  docker compose exec -T world sh -c 'cat /cache/bot-feed.log 2>/dev/null' | filter | tail -n "$n"
fi
