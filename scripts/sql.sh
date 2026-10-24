#!/bin/bash
# Runs one SQL statement against a database of the stack: scripts/sql.sh <db> "<sql>"  (db: world, character, auth). Output capped at 40 lines.
cd "$(dirname "$0")/.."
pw="$(grep -E '^DB_ROOT_PASSWORD=' .env | cut -d= -f2-)"
docker compose exec -T db mariadb -uroot -p"$pw" "nexus_forever_$1" -e "$2" </dev/null 2>&1 | head -40
