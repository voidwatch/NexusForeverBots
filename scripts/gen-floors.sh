#!/usr/bin/env bash
# Builds prop floor-height sidecars (data/map/<Asset>.nffloor) from the client archive (read only) for instance worlds, one process per
# world (big adventures need ~1 GB). Default: the dungeon/raid/expedition/adventure worlds the bots test. See docs/ASSUMPTIONS.md.
# Static .m3 props are included; when that sidecar exceeds NF_FLOOR_MAX_MB (64) the world is rebuilt from buildings only (.dgn/.i3).
#   scripts/gen-floors.sh [world id ...]
cd "$(dirname "$0")/.."
worlds="${*:-382 1263 1271 1336 2980 3009 3173 3522 3404 1627 2183 2188 2149 3180 1232 1319 1149 1181 1233 1323 1393 1437 3176 1333 1462 3032 3040}"
max=$(( ${NF_FLOOR_MAX_MB:-64} * 1048576 ))
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
for w in $worlds; do
  python3 scripts/research/floorgen.py --m3 -o "$tmp" "$w" 2>&1 | sed -E 's/\{[^}]*\}, //' | cut -c1-150
  for f in "$tmp"/*.nffloor; do
    [ -e "$f" ] || continue
    if [ "$(stat -c %s "$f")" -gt "$max" ]; then
      echo "  $(basename "$f") over $((max / 1048576)) MB with m3 props, buildings only"; rm -f "$f"
      python3 scripts/research/floorgen.py -o data/map "$w" 2>&1 | sed -E 's/\{[^}]*\}, //' | cut -c1-150
    else mv "$f" data/map/; fi
  done
done
