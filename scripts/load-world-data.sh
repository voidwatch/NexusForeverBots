#!/bin/bash
# Applies the optional upstream world data (creature spawns etc.) to nexus_forever_world.
# Run AFTER the world container has started once (it creates the schema).
# Only the open-world zones are loaded (Alizar, Isigrol, Olyssia). They match this fork's schema and each file starts with
# a DELETE for its own zone, so re-running is safe. Instance/ (expeditions, arenas, tutorial) is skipped on purpose: it was
# written in 2024+ for a newer NexusForever schema (entity_event, creature_info, ...) that this fork does not have.
set -euo pipefail
cd "$(dirname "$0")/.."
set -a
# shellcheck disable=SC1091
. ./.env
set +a
SRC="${1:-./world-data}"
# Last upstream revision that matches this fork's schema (2021-03). The zone files loaded below have not changed since.
REF="${WORLD_DATA_REF:-1e5ff92}"
if [ ! -d "$SRC/.git" ]; then
  git clone https://github.com/NexusForever/NexusForever.WorldDatabase "$SRC"
fi
# safe.directory: the checkout may belong to another user (the one docker or a previous install ran as)
GIT=(git -c safe.directory='*' -C "$SRC")
"${GIT[@]}" fetch --quiet --unshallow 2>/dev/null || true
"${GIT[@]}" checkout --quiet "$REF"
find "$SRC/Alizar" "$SRC/Isigrol" "$SRC/Olyssia" -name '*.sql' | sort | while read -r f; do
  echo "applying $f"
  docker compose exec -T db mariadb -u"$DB_USER" -p"$DB_PASSWORD" nexus_forever_world < "$f"
done
