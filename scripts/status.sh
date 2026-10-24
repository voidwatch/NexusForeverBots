#!/bin/bash
# one-screen digest: gate results, top failures, CPU/mem, git state
cd "$(dirname "$0")/.."
echo "== gate"; [ -f /tmp/gate-done.txt ] && echo "gate finished" || echo "gate not finished/not run"
[ -f /tmp/gate-fail.txt ] && { grep -E "^quest-coverage" /tmp/gate-fail.txt; grep -A3 "== system-fail" /tmp/gate-fail.txt | cut -c1-160; }
if [ -f /tmp/gate-fail.txt ]; then
  ex=$(grep -cE 'map-update-exception|spell-update-exception' /tmp/gate-fail.txt)
  [ "$ex" = 0 ] && echo "exceptions: PASS" || echo "exceptions: FAIL ($ex lines)"
  # character saves: every failed batch is counted under character-save-failed (cause fk/concurrency/duplicate-*/other), 0 is required
  cs=$(grep -E '^== character-save-failed ' /tmp/gate-fail.txt | grep -oE '[0-9]+ total' | grep -oE '[0-9]+'); cs=${cs:-0}
  [ "$cs" = 0 ] && echo "character saves: PASS" || { echo "character saves: FAIL ($cs)"; grep -A4 '^== character-save-failed ' /tmp/gate-fail.txt | tail -n +2 | cut -c1-160; }
  echo "== top failures"; head -12 /tmp/gate-fail.txt | cut -c1-120
fi
echo "== maps"; head -6 /tmp/gate-maps.txt 2>/dev/null | cut -c1-120
echo "== stats"; tail -1 /tmp/gate-stats.txt 2>/dev/null | cut -c1-140
echo "== git"; git status --short | head -5; git log --oneline | head -3
