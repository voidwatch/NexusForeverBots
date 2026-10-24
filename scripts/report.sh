#!/usr/bin/env bash
# Print reports from the running world server.
#
#   scripts/report.sh           failure report: unhandled spell effects and prerequisites, failed casts, unmatched quest kills, stalled quests
#   scripts/report.sh quests    quest coverage: every quest by objective type and what bots can already do
#
# The failure report is rewritten every 30 seconds while anything is being recorded and starts from zero at each restart.
# The quest coverage file is written the first time a bot looks at quests after a start.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

case "${1:-failures}" in
  quests)   file=quest-coverage.txt ;;
  failures) file=failure-report.txt ;;
  *) echo "Use: scripts/report.sh [failures|quests]" >&2; exit 1 ;;
esac

docker compose exec -T world sh -c "cat /cache/$file 2>/dev/null || echo 'Not written yet, give it a minute after the bots start.'" </dev/null
