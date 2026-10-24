#!/usr/bin/env bash
# Removes what install.sh created for this checkout's compose project: the containers and the network, and, only after
# you confirm, the database and cache volumes (accounts, characters, bots, quest data: this is data loss), the locally
# built images and .env.
#
# It does NOT touch your WildStar client folder (CLIENT_DIR), data/tbl, data/map, the repository files, Docker itself,
# or anything outside this repository folder.
#
#   ./uninstall.sh                      interactive: removes containers, asks about volumes, images and .env
#   ./uninstall.sh --yes                containers and network only (volumes, images and .env are kept)
#   ./uninstall.sh --yes --volumes      also delete the database and cache volumes, no question asked
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

YES=0; VOLUMES=0; IMAGES=0; ENVFILE=0
usage() {
  cat <<'EOF2'
Usage: ./uninstall.sh [options]
  -y, --yes      do not prompt; only what the flags below ask for is removed besides the containers
      --volumes  delete the database and cache volumes (DATA LOSS: accounts, characters, world data)
      --images   delete the images used by this project (the built world/auth/sts images, and mariadb if nothing else uses it)
      --env      delete .env (it holds the database passwords; keep it if you keep the volumes)
  -h, --help     this text
EOF2
}
while [ $# -gt 0 ]; do
  case "$1" in
    -y|--yes) YES=1 ;;
    --volumes) VOLUMES=1 ;;
    --images) IMAGES=1 ;;
    --env) ENVFILE=1 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 1 ;;
  esac
  shift
done

ask() {
  local q="$1" reply
  if [ "$YES" -eq 1 ] || [ ! -t 0 ]; then return 1; fi
  read -rp "$q [y/N] " reply
  [[ "${reply:-n}" =~ ^[Yy] ]]
}

command -v docker >/dev/null 2>&1 || { echo "docker is not installed, nothing to remove." >&2; exit 1; }
PROJECT="nexusforeverbots"
if [ -f .env ]; then
  set -a
  # shellcheck disable=SC1091
  . ./.env
  set +a
  PROJECT="${COMPOSE_PROJECT_NAME:-$PROJECT}"
else
  echo "No .env here; using the default project name $PROJECT."
  # compose needs the variables the file references; empty values are enough to stop and remove
  export DB_USER="" DB_PASSWORD="" DB_ROOT_PASSWORD="" REALM_HOST=""
fi

echo "This removes the Docker containers and network of compose project '$PROJECT' (this folder: $(pwd))."
echo "It does NOT touch your WildStar client folder, data/tbl, data/map, the repository files or anything outside this folder."
echo
echo "Containers of project $PROJECT:"
docker compose ps -a --format '  {{.Name}}  {{.Status}}' || true

if [ "$YES" -eq 0 ] && [ -t 0 ]; then
  read -rp "Stop and remove these containers? [y/N] " reply
  [[ "${reply:-n}" =~ ^[Yy] ]] || { echo "Nothing removed."; exit 0; }
fi
if [ "$VOLUMES" -eq 0 ] && ask "Also DELETE the volumes ${PROJECT}_db-data and ${PROJECT}_world-cache? All accounts, characters and loaded world data are lost."; then
  VOLUMES=1
fi
if [ "$IMAGES" -eq 0 ] && ask "Also delete the images (${IMAGE_PREFIX:-$PROJECT}-world/-auth/-sts:local, mariadb:10.11 if unused)? They are rebuilt or pulled on the next install."; then
  IMAGES=1
fi

args=(down --remove-orphans)
[ "$VOLUMES" -eq 1 ] && args+=(--volumes)
docker compose "${args[@]}"
if [ "$IMAGES" -eq 1 ]; then
  # a separate step: Docker refuses to delete an image another container still uses (e.g. mariadb for a second stack),
  # which is fine and must not stop the uninstall
  docker compose down --rmi all || echo "warn: some images were not deleted (still used by other containers?)"
fi
echo "removed containers and network of $PROJECT$( [ "$VOLUMES" -eq 1 ] && echo ', volumes deleted' || echo ', volumes kept')$( [ "$IMAGES" -eq 1 ] && echo ', images deleted')"

if [ -f .env ]; then
  if [ "$ENVFILE" -eq 0 ] && [ "$VOLUMES" -eq 1 ] && ask "Delete .env too (its passwords belong to the deleted database)?"; then ENVFILE=1; fi
  if [ "$ENVFILE" -eq 1 ]; then rm -f .env; echo "deleted .env"; else echo "kept .env"; fi
fi
[ "$VOLUMES" -eq 1 ] || echo "The volumes were kept: ./install.sh brings the same server back with its data. Delete them with ./uninstall.sh --volumes."
