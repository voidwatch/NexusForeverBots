#!/usr/bin/env bash
# Run a console command on the world server from the terminal and print the answer, no game client needed.
#
#   scripts/gt.sh gt show Creature2 26016
#   scripts/gt.sh gt find Creature2 12545
#   scripts/gt.sh gt findall 12545
#   scripts/gt.sh gt effects
#   scripts/gt.sh gt report
#   scripts/gt.sh bot list
#
# Anything the world console accepts works. The server picks commands up within a second.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

[ $# -gt 0 ] || { sed -n '2,12p' "$0"; exit 1; }

id="$(date +%s)$RANDOM"
export CMD="$*" ID="$id"

docker compose exec -T -e "CMD=$CMD" -e "ID=$ID" world sh -c 'printf "%s\t%s\n" "$ID" "$CMD" >> /cache/commands.txt' </dev/null

for _ in $(seq 1 $(( ${GT_WAIT:-20} * 2 ))); do
  if docker compose exec -T -e "ID=$ID" world sh -c 'test -f "/cache/out/$ID.txt.done"' </dev/null 2>/dev/null; then
    docker compose exec -T -e "ID=$ID" world sh -c 'cat "/cache/out/$ID.txt" 2>/dev/null; rm -f "/cache/out/$ID.txt" "/cache/out/$ID.txt.done"' </dev/null
    exit 0
  fi
  sleep 0.5
done

echo "No answer after ${GT_WAIT:-20} seconds. Is the world server up? (docker compose ps)" >&2
exit 1
