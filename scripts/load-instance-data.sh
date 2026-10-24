#!/bin/bash
# Loads every upstream Instance/*.sql file (expeditions, arenas, tutorial) that fits this fork's schema. The files are written for a newer NexusForever schema, so every statement that touches
# a table this fork does not have (entity_event, entity_script, entity_property, creature_info_property, map_entrance) is dropped and
# only entity, entity_stats and entity_spline rows are applied. Each file deletes the entities of its world first, so re-running is safe.
# Run after load-world-data.sh (it uses the same checkout of NexusForever.WorldDatabase in ./world-data).
set -euo pipefail
cd "$(dirname "$0")/.."
set -a
# shellcheck disable=SC1091
. ./.env
set +a

GIT=(git -c safe.directory='*' -C world-data)
"${GIT[@]}" fetch --quiet origin master

while IFS= read -r file; do
  echo "applying $file"
  "${GIT[@]}" show "origin/master:$file" | python3 -c '
import re, sys
text = sys.stdin.read()
drop = re.compile(r"^\s*(INSERT INTO|DELETE FROM|UPDATE)\s+`?(entity_event|entity_script|entity_property|creature_info_property|creature_info|map_entrance)`?\b", re.I | re.M)
known = {"id","type","creature","world","area","x","y","z","rx","ry","rz","displayinfo","outfitinfo","faction1","faction2","questchecklistidx","activepropid","worldsocketid"}
def trim(body):
    # INSERT INTO `entity` with columns this fork does not have (Mode and similar): drop those columns and their values in every row
    m = re.match(r"(INSERT INTO\s+`entity`\s*)\(([^)]*)\)(\s*VALUES?\s*)(.*)$", body, re.S | re.I)
    if not m:
        return body
    cols = [c.strip().strip("`") for c in m.group(2).split(",")]
    keep = [i for i, c in enumerate(cols) if c.lower() in known]
    if len(keep) == len(cols):
        return body
    rows = re.findall(r"\(([^()]*(?:\([^()]*\)[^()]*)*)\)\s*(?:,|$)", m.group(4).strip())
    out_rows = []
    for row in rows:
        vals = [v.strip() for v in row.split(",")]
        out_rows.append("(" + ", ".join(vals[i] for i in keep) + ")")
    return m.group(1) + "(" + ", ".join("`" + cols[i] + "`" for i in keep) + ")" + m.group(3) + ",\n".join(out_rows)
out = []
for stmt in re.split(r";\s*\n", text):
    body = "\n".join(l for l in stmt.splitlines() if not l.strip().startswith("--")).strip()
    if not body or drop.match(body):
        continue
    out.append(trim(body) + ";")
print("\n".join(out))
' | docker compose exec -T db mariadb -u"$DB_USER" -p"$DB_PASSWORD" nexus_forever_world
done < <("${GIT[@]}" ls-tree -r --name-only origin/master | grep '^Instance/')
echo "done"
