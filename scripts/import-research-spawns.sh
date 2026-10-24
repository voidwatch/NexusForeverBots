#!/usr/bin/env bash
# Loads data/spawns/research-spawns.tsv (made by scripts/research/jabbithole_match.py) and data/spawns/*.tsv
# (questloc_spawns.py and other placement scripts, same columns: creature, world, x, z, source) into the world database as entity rows 9000000+.
# The world server fills in terrain heights (gt importspawns); re-running replaces the previous research rows. Restart the world afterwards.
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
cat data/spawns/*.tsv 2>/dev/null | docker compose exec -T world sh -c 'cat > /cache/research-spawns.tsv'
start=$(date +%s)
scripts/gt.sh gt importspawns || true
# the command takes about a minute for 60,000 rows; the sql file is written at the end
for _ in $(seq 1 90); do
  t=$(docker compose exec -T world sh -c 'stat -c %Y /cache/research-spawns.sql' 2>/dev/null || echo 0)
  [ "${t:-0}" -ge "$start" ] && break
  sleep 5
done
[ "${t:-0}" -ge "$start" ] || { echo "importspawns did not finish" >&2; exit 1; }
docker compose exec -T world cat /cache/research-spawns.sql | docker compose exec -T db mariadb -u"$DB_USER" -p"$DB_PASSWORD" nexus_forever_world
docker compose exec -T db mariadb -u"$DB_USER" -p"$DB_PASSWORD" nexus_forever_world -N -e "select count(*) from entity where id>=9000000"
echo "imported by source:"; docker compose exec -T world cat /cache/research-spawns-sources.txt
